/* keep POSIX declarations (mmap etc.) visible under -std=c11 on glibc */
#define _DEFAULT_SOURCE

#include "mymalloc.h"

#include <assert.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>

#define SLOT_ALIGN ((size_t)16)

struct my_pool
{
    unsigned char *base;
    size_t bytes;          /* slot_stride * slot_count, the mmap'd extent */
    size_t slot_stride;    /* requested slot size rounded up to SLOT_ALIGN */
    size_t slot_count;
    void *free_head;       /* intrusive singly-linked list through free slots */
    size_t free_count;
    size_t fallback_count;
};

my_pool_t *my_pool_create(size_t slot_size, size_t slot_count)
{
    if (slot_size == 0 || slot_count == 0)
        return NULL;
    size_t stride = (slot_size + (SLOT_ALIGN - 1)) & ~(SLOT_ALIGN - 1);
    if (stride < slot_size || stride > SIZE_MAX / slot_count)
        return NULL;
    size_t bytes = stride * slot_count;

    my_pool_t *pool = malloc(sizeof *pool);
    if (pool == NULL)
        return NULL;
    void *base = mmap(NULL, bytes, PROT_READ | PROT_WRITE,
                      MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (base == MAP_FAILED)
    {
        free(pool);
        return NULL;
    }

    /* Pre-touch every page: all zero-fill page faults are paid here, once,
       so they can never appear on the allocation hot path. */
    memset(base, 0, bytes);

    pool->base = base;
    pool->bytes = bytes;
    pool->slot_stride = stride;
    pool->slot_count = slot_count;
    pool->free_head = NULL;
    for (size_t i = slot_count; i-- > 0;)
    {
        void *slot = pool->base + i * stride;
        *(void **)slot = pool->free_head;
        pool->free_head = slot;
    }
    pool->free_count = slot_count;
    pool->fallback_count = 0;
    return pool;
}

void my_pool_destroy(my_pool_t *pool)
{
    if (pool == NULL)
        return;
    munmap(pool->base, pool->bytes);
    free(pool);
}

void *my_pool_alloc(my_pool_t *pool, size_t size)
{
    if (size <= pool->slot_stride && pool->free_head != NULL)
    {
        void *p = pool->free_head;
        pool->free_head = *(void **)p;
        pool->free_count--;
        return p;
    }
    pool->fallback_count++;
    return malloc(size);
}

void my_pool_free(my_pool_t *pool, void *p)
{
    if (p == NULL)
        return;
    if (my_pool_owns(pool, p))
    {
        assert(((size_t)((unsigned char *)p - pool->base)) % pool->slot_stride == 0);
        *(void **)p = pool->free_head;
        pool->free_head = p;
        pool->free_count++;
        return;
    }
    free(p);
}

bool my_pool_owns(const my_pool_t *pool, const void *p)
{
    const unsigned char *q = p;
    return q >= pool->base && q < pool->base + pool->bytes;
}

size_t my_pool_slot_stride(const my_pool_t *pool) { return pool->slot_stride; }
size_t my_pool_free_slots(const my_pool_t *pool) { return pool->free_count; }
size_t my_pool_fallback_count(const my_pool_t *pool) { return pool->fallback_count; }

/* ---------- process-wide default pool ---------- */

#define MY_DEFAULT_SLOT_SIZE ((size_t)40000) /* the assignment's 10,000 ints */
#define MY_DEFAULT_SLOT_COUNT ((size_t)64)

static my_pool_t *g_default_pool;
static int g_default_failed;

int myMallocInit(size_t slot_size, size_t slot_count)
{
    if (g_default_pool != NULL)
        return -1;
    g_default_pool = my_pool_create(slot_size, slot_count);
    return g_default_pool != NULL ? 0 : -1;
}

void *myMalloc(size_t size)
{
    if (g_default_pool == NULL)
    {
        if (g_default_failed ||
            myMallocInit(MY_DEFAULT_SLOT_SIZE, MY_DEFAULT_SLOT_COUNT) != 0)
        {
            g_default_failed = 1; /* degraded to plain malloc, still correct */
            return malloc(size);
        }
    }
    return my_pool_alloc(g_default_pool, size);
}

void myFree(void *p)
{
    if (g_default_pool != NULL)
    {
        my_pool_free(g_default_pool, p);
        return;
    }
    free(p);
}

void myMallocShutdown(void)
{
    my_pool_destroy(g_default_pool);
    g_default_pool = NULL;
    g_default_failed = 0;
}
