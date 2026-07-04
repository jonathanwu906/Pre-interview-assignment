/* glibc hides clock_gettime/CLOCK_MONOTONIC_RAW under -std=c11 without this;
   no-op on macOS */
#define _GNU_SOURCE

#include "mymalloc.h"

#include <inttypes.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

/*
 * Four scenarios, one harness, two backends (system malloc vs myMalloc pool).
 * Each op = alloc + touch one byte per page + free, so first-touch page
 * faults — when an allocator incurs them — land inside the measurement.
 * Latency distribution (p50/p99/p99.9/max) is reported alongside the batch
 * mean because per-op timestamps have a floor of ~2× timer overhead, which
 * is measured and printed first.
 */

#ifndef CLOCK_MONOTONIC_RAW
#define CLOCK_MONOTONIC_RAW CLOCK_MONOTONIC /* POSIX fallback */
#endif

static uint64_t now_ns(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC_RAW, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
}

/* xorshift64* with a fixed seed — deterministic salt/topology on every run */
static uint64_t g_rng_state = 42;
static uint64_t rng_next(void)
{
    g_rng_state ^= g_rng_state >> 12;
    g_rng_state ^= g_rng_state << 25;
    g_rng_state ^= g_rng_state >> 27;
    return g_rng_state * 2685821657736338717ULL;
}

typedef void *(*alloc_fn)(void *ctx, size_t size);
typedef void (*free_fn)(void *ctx, void *p);

static void *sys_alloc(void *ctx, size_t size) { (void)ctx; return malloc(size); }
static void sys_free(void *ctx, void *p) { (void)ctx; free(p); }
static void *pool_alloc(void *ctx, size_t size) { return my_pool_alloc(ctx, size); }
static void pool_free(void *ctx, void *p) { my_pool_free(ctx, p); }

static size_t g_page;
static int g_checksum_mismatch; /* set if any scenario's two backends disagree; drives exit code */

/* One measured pass. Calls go through volatile function pointers so the
   compiler cannot fold or elide the alloc/free pair. */
static uint64_t run_loop(alloc_fn af_in, free_fn ff_in, void *ctx, size_t size,
                         long iters, uint64_t *durations, uint64_t *checksum_out)
{
    alloc_fn volatile af = af_in;
    free_fn volatile ff = ff_in;
    uint64_t checksum = 0;
    uint64_t t_batch0 = now_ns();
    for (long i = 0; i < iters; i++)
    {
        uint64_t t0 = now_ns();
        unsigned char *p = af(ctx, size);
        for (size_t off = 0; off < size; off += g_page)
            p[off] = (unsigned char)off;
        p[size - 1] = 0xA5;
        checksum += p[0] + p[size - 1];
        ff(ctx, p);
        uint64_t t1 = now_ns();
        if (durations != NULL)
            durations[i] = t1 - t0;
    }
    uint64_t batch = now_ns() - t_batch0;
    if (checksum_out != NULL)
        *checksum_out = checksum;
    return batch;
}

static int cmp_u64(const void *a, const void *b)
{
    uint64_t x = *(const uint64_t *)a, y = *(const uint64_t *)b;
    return (x > y) - (x < y);
}

static uint64_t pct(const uint64_t *sorted, long n, double q)
{
    long idx = (long)(q * (double)(n - 1));
    return sorted[idx];
}

static void report(const char *label, long iters, uint64_t batch_ns,
                   uint64_t *durations, uint64_t checksum)
{
    qsort(durations, (size_t)iters, sizeof durations[0], cmp_u64);
    printf("  %-10s mean %8.1f ns | p50 %6" PRIu64 " | p99 %6" PRIu64
           " | p99.9 %7" PRIu64 " | max %9" PRIu64 " ns | checksum %" PRIu64 "\n",
           label, (double)batch_ns / (double)iters, pct(durations, iters, 0.50),
           pct(durations, iters, 0.99), pct(durations, iters, 0.999),
           durations[iters - 1], checksum);
}

/* warmup pass, then measured pass */
static uint64_t measure(alloc_fn af, free_fn ff, void *ctx, size_t size, long iters,
                        uint64_t *durations, uint64_t *checksum_out)
{
    run_loop(af, ff, ctx, size, iters / 100 + 1, NULL, NULL);
    return run_loop(af, ff, ctx, size, iters, durations, checksum_out);
}

static void scenario_pair(const char *title, size_t size, long iters,
                          size_t pool_slots, uint64_t *durations)
{
    printf("%s\n", title);
    uint64_t sum_sys, sum_pool;

    uint64_t batch_sys = measure(sys_alloc, sys_free, NULL, size, iters, durations, &sum_sys);
    report("malloc", iters, batch_sys, durations, sum_sys);

    uint64_t t0 = now_ns();
    my_pool_t *pool = my_pool_create(size, pool_slots);
    uint64_t init_ns = now_ns() - t0;
    if (pool == NULL)
    {
        printf("  pool creation FAILED\n");
        return;
    }
    uint64_t batch_pool = measure(pool_alloc, pool_free, pool, size, iters, durations, &sum_pool);
    report("myMalloc", iters, batch_pool, durations, sum_pool);

    if (sum_sys != sum_pool)
        g_checksum_mismatch = 1;
    printf("  speedup (mean) %.1fx | checksums %s | pool fallbacks %zu | one-time pool init %" PRIu64
           " us (%zu slots x %zu B, pre-touched)\n\n",
           (double)batch_sys / (double)batch_pool,
           sum_sys == sum_pool ? "identical" : "MISMATCH",
           my_pool_fallback_count(pool), init_ns / 1000, pool_slots,
           my_pool_slot_stride(pool));
    my_pool_destroy(pool);
}

/* ---------- S4: threads, one pool per thread ---------- */

typedef struct
{
    int use_pool;
    size_t size;
    long iters;
    uint64_t *durations;
    uint64_t batch_ns;
    uint64_t checksum;
} thread_arg_t;

static void *thread_main(void *arg_in)
{
    thread_arg_t *arg = arg_in;
    if (arg->use_pool)
    {
        my_pool_t *pool = my_pool_create(arg->size, 64);
        arg->batch_ns = measure(pool_alloc, pool_free, pool, arg->size, arg->iters,
                                arg->durations, &arg->checksum);
        my_pool_destroy(pool);
    }
    else
    {
        arg->batch_ns = measure(sys_alloc, sys_free, NULL, arg->size, arg->iters,
                                arg->durations, &arg->checksum);
    }
    return NULL;
}

static void scenario_threads(size_t size, long iters_per_thread, int nthreads,
                             uint64_t *durations)
{
    printf("S4 contention: %d threads x %ld iters x %zu B (pool side: one private pool per thread)\n",
           nthreads, iters_per_thread, size);
    uint64_t checksums[2] = { 0, 0 };
    for (int use_pool = 0; use_pool <= 1; use_pool++)
    {
        pthread_t tids[16];
        thread_arg_t args[16];
        for (int t = 0; t < nthreads; t++)
        {
            args[t] = (thread_arg_t){ use_pool, size, iters_per_thread,
                                      durations + (long)t * iters_per_thread, 0, 0 };
            pthread_create(&tids[t], NULL, thread_main, &args[t]);
        }
        uint64_t wall = 0, checksum = 0;
        for (int t = 0; t < nthreads; t++)
        {
            pthread_join(tids[t], NULL);
            if (args[t].batch_ns > wall)
                wall = args[t].batch_ns;
            checksum += args[t].checksum;
        }
        checksums[use_pool] = checksum;
        long total = (long)nthreads * iters_per_thread;
        report(use_pool ? "myMalloc" : "malloc", total, wall * (uint64_t)nthreads, durations,
               checksum);
    }
    if (checksums[0] != checksums[1])
        g_checksum_mismatch = 1;
    printf("  checksums %s\n\n", checksums[0] == checksums[1] ? "identical" : "MISMATCH");
}

int main(int argc, char **argv)
{
    long iters_s1 = argc > 1 ? atol(argv[1]) : 1000000;
    long iters_s2 = argc > 2 ? atol(argv[2]) : 50000;
    int nthreads = argc > 3 ? atoi(argv[3]) : 4;
    if (nthreads > 16)
        nthreads = 16;
    g_page = (size_t)sysconf(_SC_PAGESIZE);

    long max_samples = iters_s1 > (long)nthreads * (iters_s1 / 4) ? iters_s1
                                                                  : (long)nthreads * (iters_s1 / 4);
    uint64_t *durations = malloc((size_t)max_samples * sizeof *durations);
    if (durations == NULL)
        return 1;

    /* timer floor: per-op numbers cannot resolve below this */
    uint64_t o0 = now_ns();
    for (int i = 0; i < 1000000; i++)
    {
        uint64_t a = now_ns();
        (void)a;
    }
    printf("env: page %zu B, %ld cores | timer overhead ~%.0f ns/pair\n\n", g_page,
           sysconf(_SC_NPROCESSORS_ONLN), (double)(now_ns() - o0) / 1000000.0 * 2.0);

    printf("S1 assignment pattern: 10,000 ints = 40,000 B x %ld iters, single thread\n", iters_s1);
    scenario_pair("", 40000, iters_s1, 64, durations);

    printf("S2a large allocations: 262,144 B x %ld iters\n", iters_s2);
    scenario_pair("", 262144, iters_s2, 8, durations);

    printf("S2b large allocations: 4 MB x %ld iters (VM / zero-fill regime)\n", iters_s2 / 2);
    scenario_pair("", (size_t)4 << 20, iters_s2 / 2, 4, durations);

    /* S3: same loop as S1, but on a deliberately fragmented heap.
       Salt: 50,000 mixed-size blocks (90% 16 B–1 KB, 10% 4–64 KB), every
       other one freed and kept freed while measuring. */
    {
        enum { SALT = 50000 };
        static void *salt[SALT];
        for (int i = 0; i < SALT; i++)
        {
            size_t size = (rng_next() % 10 == 0) ? 4096 + rng_next() % 61441
                                                 : 16 + rng_next() % 1009;
            salt[i] = malloc(size);
            if (salt[i] != NULL)
                memset(salt[i], 0x5A, 1);
        }
        for (int i = 0; i < SALT; i += 2)
        {
            free(salt[i]);
            salt[i] = NULL;
        }
        printf("S3 fragmented heap: S1 rerun after salting (%d mixed-size blocks, half freed)\n",
               SALT);
        scenario_pair("", 40000, iters_s1, 64, durations);
        for (int i = 1; i < SALT; i += 2)
            free(salt[i]);
    }

    scenario_threads(40000, iters_s1 / 4, nthreads, durations);

    free(durations);
    return g_checksum_mismatch ? 1 : 0;
}
