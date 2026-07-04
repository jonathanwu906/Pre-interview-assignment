#ifndef MYMALLOC_H
#define MYMALLOC_H

#include <stdbool.h>
#include <stddef.h>

/*
 * Fixed-size slot pool with an intrusive free list.
 *
 * Contract:
 *  - A pool serves allocations of size <= its slot size from pre-faulted
 *    memory in O(1) with no syscall, no search, no page fault. Larger
 *    requests, and requests arriving when the pool is exhausted, fall back
 *    to system malloc transparently.
 *  - Every pointer must be released through the pool that produced it
 *    (my_pool_free routes to the slot list or to system free by an O(1)
 *    address-range check). Same-ownership rule as malloc/free themselves.
 *  - Returned pointers are aligned to at least 16 bytes.
 *  - A pool is deliberately NOT thread-safe: the intended multi-thread
 *    pattern is one pool per thread (zero sharing → zero locks). See the
 *    design doc for the shared-pool evolution path.
 *  - my_pool_destroy invalidates all outstanding slot pointers.
 */
typedef struct my_pool my_pool_t;

my_pool_t *my_pool_create(size_t slot_size, size_t slot_count);
void       my_pool_destroy(my_pool_t *pool);
void      *my_pool_alloc(my_pool_t *pool, size_t size);
void       my_pool_free(my_pool_t *pool, void *p);
bool       my_pool_owns(const my_pool_t *pool, const void *p);

size_t my_pool_slot_stride(const my_pool_t *pool);
size_t my_pool_free_slots(const my_pool_t *pool);
size_t my_pool_fallback_count(const my_pool_t *pool);

/*
 * Drop-in convenience API over one process-wide default pool.
 * myMallocInit is optional: the first myMalloc call auto-initializes the
 * default pool (slot = 40,000 bytes — the assignment's 10,000 ints — × 64
 * slots). Init after first use returns -1. Not thread-safe (see above).
 */
int   myMallocInit(size_t slot_size, size_t slot_count);
void *myMalloc(size_t size);
void  myFree(void *p);
void  myMallocShutdown(void);

#endif
