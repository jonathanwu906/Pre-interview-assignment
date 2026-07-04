CC      ?= cc
CFLAGS  ?= -std=c11 -O2 -Wall -Wextra -Werror

all: tests bench

tests: mymalloc.c tests.c mymalloc.h
	$(CC) $(CFLAGS) -o $@ mymalloc.c tests.c

bench: mymalloc.c bench.c mymalloc.h
	$(CC) $(CFLAGS) -DNDEBUG -o $@ mymalloc.c bench.c -lpthread

test: tests
	./tests

test-asan: mymalloc.c tests.c mymalloc.h
	$(CC) -std=c11 -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -o tests-asan mymalloc.c tests.c
	./tests-asan

run-bench: bench
	./bench

clean:
	rm -rf tests bench tests-asan *.o *.dSYM

.PHONY: all test test-asan run-bench clean
