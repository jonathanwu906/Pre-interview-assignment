#include "mymalloc.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The 13 test cases from design doc §7 — correctness, routing, edge cases.
   Zero test-framework dependencies: `make test` runs them directly. */

static int g_passed, g_total;

static void check(const char *name, int ok)
{
    g_total++;
    if (ok)
        g_passed++;
    printf("%s  %s\n", ok ? "PASS" : "FAIL", name);
}

/* xorshift64* with a fixed seed — deterministic on every run and platform */
static uint64_t g_rng_state;
static uint64_t rng_next(void)
{
    g_rng_state ^= g_rng_state >> 12;
    g_rng_state ^= g_rng_state << 25;
    g_rng_state ^= g_rng_state >> 27;
    return g_rng_state * 2685821657736338717ULL;
}

static int cmp_ptr(const void *a, const void *b)
{
    uintptr_t x = *(const uintptr_t *)a, y = *(const uintptr_t *)b;
    return (x > y) - (x < y);
}

/* Random alloc/free sequence with content tags, driven through an arbitrary
   backend. Returns 1 iff every block's tags were intact at free time.
   Same seed → identical operation sequence for every backend (test 10). */
typedef struct
{
    void *(*alloc)(void *ctx, size_t size);
    void (*dealloc)(void *ctx, void *p);
    void *ctx;
} backend_t;

static void *pool_alloc_cb(void *ctx, size_t size) { return my_pool_alloc(ctx, size); }
static void pool_free_cb(void *ctx, void *p) { my_pool_free(ctx, p); }
static void *sys_alloc_cb(void *ctx, size_t size) { (void)ctx; return malloc(size); }
static void sys_free_cb(void *ctx, void *p) { (void)ctx; free(p); }

#define STRESS_OPS 100000
#define STRESS_MAX_LIVE 512

static int run_stress(const backend_t *b, uint64_t seed)
{
    struct { unsigned char *p; size_t size; unsigned char tag; } live[STRESS_MAX_LIVE];
    size_t n_live = 0;
    int ok = 1;
    g_rng_state = seed;
    for (int op = 0; op < STRESS_OPS; op++)
    {
        if (n_live < STRESS_MAX_LIVE && (rng_next() % 2 == 0 || n_live == 0))
        {
            size_t size = 1 + rng_next() % 8192;
            unsigned char tag = (unsigned char)(rng_next() & 0xFF);
            unsigned char *p = b->alloc(b->ctx, size);
            if (p == NULL)
                return 0;
            p[size - 1] = (unsigned char)~tag;
            p[0] = tag; /* for size==1 this overwrites: p[0] is authoritative */
            live[n_live].p = p;
            live[n_live].size = size;
            live[n_live].tag = tag;
            n_live++;
        }
        else
        {
            size_t i = rng_next() % n_live;
            ok &= live[i].p[0] == live[i].tag;
            if (live[i].size > 1)
                ok &= live[i].p[live[i].size - 1] == (unsigned char)~live[i].tag;
            b->dealloc(b->ctx, live[i].p);
            live[i] = live[n_live - 1];
            n_live--;
        }
    }
    while (n_live > 0)
    {
        n_live--;
        ok &= live[n_live].p[0] == live[n_live].tag;
        b->dealloc(b->ctx, live[n_live].p);
    }
    return ok;
}

int main(void)
{
    /* 1. the assignment's own pattern: 10,000 ints via the drop-in API */
    {
        int *ptr = myMalloc(10000 * sizeof(int));
        int ok = ptr != NULL && ((uintptr_t)ptr & 15) == 0;
        long long sum = 0;
        if (ok)
        {
            for (int i = 0; i < 10000; i++)
                ptr[i] = i;
            for (int i = 0; i < 10000; i++)
                sum += ptr[i];
        }
        myFree(ptr);
        myMallocShutdown();
        check("1 assignment pattern (10,000 ints) → usable, 16-aligned", ok && sum == 49995000LL);
    }

    /* 2. drained pool: slots aligned, disjoint, exactly stride apart */
    {
        my_pool_t *pool = my_pool_create(40000, 8);
        uintptr_t ptrs[8];
        int ok = pool != NULL && my_pool_slot_stride(pool) == 40000;
        for (int i = 0; i < 8; i++)
        {
            void *p = my_pool_alloc(pool, 40000);
            ok &= p != NULL && my_pool_owns(pool, p) && ((uintptr_t)p & 15) == 0;
            ptrs[i] = (uintptr_t)p;
        }
        qsort(ptrs, 8, sizeof ptrs[0], cmp_ptr);
        for (int i = 1; i < 8; i++)
            ok &= ptrs[i] - ptrs[i - 1] == 40000;
        for (int i = 0; i < 8; i++)
            my_pool_free(pool, (void *)ptrs[i]);
        ok &= my_pool_free_slots(pool) == 8;
        my_pool_destroy(pool);
        check("2 slots 16-aligned, disjoint, stride apart", ok);
    }

    /* 3. full-extent writes to every slot at once → no overlap, no corruption */
    {
        my_pool_t *pool = my_pool_create(4096, 16);
        unsigned char *ps[16];
        int ok = pool != NULL;
        for (int i = 0; i < 16; i++)
        {
            ps[i] = my_pool_alloc(pool, 4096);
            ok &= ps[i] != NULL;
            memset(ps[i], i + 1, 4096);
        }
        for (int i = 0; i < 16; i++)
            for (int off = 0; off < 4096; off += 511)
                ok &= ps[i][off] == i + 1;
        for (int i = 0; i < 16; i++)
            my_pool_free(pool, ps[i]);
        my_pool_destroy(pool);
        check("3 full-extent writes across drained pool → intact", ok);
    }

    /* 4. alloc-free-alloc returns the same slot (LIFO reuse = warm cache) */
    {
        my_pool_t *pool = my_pool_create(1024, 4);
        void *a = my_pool_alloc(pool, 1024);
        my_pool_free(pool, a);
        void *b = my_pool_alloc(pool, 1024);
        check("4 LIFO reuse: alloc-free-alloc → same slot", a != NULL && a == b);
        my_pool_free(pool, b);
        my_pool_destroy(pool);
    }

    /* 5. exhaustion → transparent malloc fallback, frees route both ways */
    {
        my_pool_t *pool = my_pool_create(1024, 4);
        void *ps[6];
        int owned = 0, ok = pool != NULL;
        for (int i = 0; i < 6; i++)
        {
            ps[i] = my_pool_alloc(pool, 1024);
            ok &= ps[i] != NULL;
            memset(ps[i], 0xEE, 1024);
            owned += my_pool_owns(pool, ps[i]);
        }
        ok &= owned == 4 && my_pool_fallback_count(pool) == 2 && my_pool_free_slots(pool) == 0;
        for (int i = 0; i < 6; i++)
            my_pool_free(pool, ps[i]);
        ok &= my_pool_free_slots(pool) == 4;
        my_pool_destroy(pool);
        check("5 exhaustion → malloc fallback, frees route by ownership", ok);
    }

    /* 6. oversize request → fallback even with free slots available */
    {
        my_pool_t *pool = my_pool_create(1024, 4);
        void *p = my_pool_alloc(pool, 4096);
        int ok = p != NULL && !my_pool_owns(pool, p) && my_pool_fallback_count(pool) == 1 &&
                 my_pool_free_slots(pool) == 4;
        memset(p, 0xCC, 4096);
        my_pool_free(pool, p);
        my_pool_destroy(pool);
        check("6 oversize request → fallback, pool untouched", ok);
    }

    /* 7. NULL frees are no-ops on both APIs */
    {
        my_pool_t *pool = my_pool_create(64, 2);
        my_pool_free(pool, NULL);
        myFree(NULL); /* no default pool initialized here either */
        my_pool_destroy(pool);
        check("7 free(NULL) → no-op", 1);
    }

    /* 8. zero-size allocation survives the round trip */
    {
        my_pool_t *pool = my_pool_create(64, 2);
        void *p = my_pool_alloc(pool, 0);
        my_pool_free(pool, p);
        int ok = my_pool_free_slots(pool) == 2;
        my_pool_destroy(pool);
        check("8 zero-size alloc/free round trip", ok);
    }

    /* 9. randomized routing stress on one pool: tags intact, pool refills */
    {
        my_pool_t *pool = my_pool_create(4096, 64);
        backend_t b = { pool_alloc_cb, pool_free_cb, pool };
        int ok = run_stress(&b, 42);
        ok &= my_pool_free_slots(pool) == 64 && my_pool_fallback_count(pool) > 0;
        my_pool_destroy(pool);
        check("9 randomized alloc/free stress → all tags intact, pool refilled", ok);
    }

    /* 10. differential parity: identical op sequence on pool vs system malloc */
    {
        my_pool_t *pool = my_pool_create(4096, 64);
        backend_t bp = { pool_alloc_cb, pool_free_cb, pool };
        backend_t bs = { sys_alloc_cb, sys_free_cb, NULL };
        int ok = run_stress(&bp, 20260704) && run_stress(&bs, 20260704);
        my_pool_destroy(pool);
        check("10 differential: same sequence on pool and malloc, both intact", ok);
    }

    /* 11. ownership predicate across pools and foreign pointers */
    {
        my_pool_t *a = my_pool_create(256, 2);
        my_pool_t *b = my_pool_create(256, 2);
        void *pa = my_pool_alloc(a, 256);
        void *pm = malloc(256);
        int ok = my_pool_owns(a, pa) && !my_pool_owns(b, pa) && !my_pool_owns(a, pm) &&
                 !my_pool_owns(b, pm);
        my_pool_free(a, pa);
        free(pm);
        my_pool_destroy(a);
        my_pool_destroy(b);
        check("11 ownership: pools disjoint, foreign pointers rejected", ok);
    }

    /* 12. constructor edge cases: zero sizes, overflow, minimal pools */
    {
        int ok = my_pool_create(0, 1) == NULL && my_pool_create(1, 0) == NULL &&
                 my_pool_create(SIZE_MAX, 2) == NULL;
        my_pool_t *tiny = my_pool_create(1, 1);
        ok &= tiny != NULL && my_pool_slot_stride(tiny) == 16;
        void *p = my_pool_alloc(tiny, 1);
        ok &= p != NULL && my_pool_owns(tiny, p);
        my_pool_free(tiny, p);
        my_pool_destroy(tiny);
        check("12 constructor edges: rejects 0/overflow, 1-byte slot rounds to 16", ok);
    }

    /* 13. lifecycle: init-once contract, lazy default init, clean shutdown */
    {
        int ok = myMallocInit(1024, 4) == 0 && myMallocInit(1024, 4) == -1;
        void *p = myMalloc(512);
        ok &= p != NULL;
        myFree(p);
        myMallocShutdown();
        void *q = myMalloc(40000); /* lazy re-init with defaults */
        ok &= q != NULL;
        myFree(q);
        myMallocShutdown();
        check("13 lifecycle: init-once, lazy default, shutdown+reinit", ok);
    }

    printf("%d/%d tests passed\n", g_passed, g_total);
    return g_passed == g_total ? 0 : 1;
}
