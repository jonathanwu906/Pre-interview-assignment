# 題目二：myMalloc — C 實作與 Testing Results

設計說明（成本解剖、解法階梯、設計決策、benchmark 方法論、否決方案）見 [`../design-doc.md`](../design-doc.md)。本文件收錄執行方式與**實際執行輸出**。

環境：Apple M2（8 核、16 KB page）、macOS 26.5.1、Apple clang 21。零外部相依，任何 POSIX + C11 環境可建置。

## 1. 測試（13 項）

```bash
make test
```

實際輸出：

```text
PASS  1 assignment pattern (10,000 ints) → usable, 16-aligned
PASS  2 slots 16-aligned, disjoint, stride apart
PASS  3 full-extent writes across drained pool → intact
PASS  4 LIFO reuse: alloc-free-alloc → same slot
PASS  5 exhaustion → malloc fallback, frees route by ownership
PASS  6 oversize request → fallback, pool untouched
PASS  7 free(NULL) → no-op
PASS  8 zero-size alloc/free round trip
PASS  9 randomized alloc/free stress → all tags intact, pool refilled
PASS  10 differential: same sequence on pool and malloc, both intact
PASS  11 ownership: pools disjoint, foreign pointers rejected
PASS  12 constructor edges: rejects 0/overflow, 1-byte slot rounds to 16
PASS  13 lifecycle: init-once, lazy default, shutdown+reinit
13/13 tests passed
```

記憶體安全另以 sanitizer 驗證：

```bash
make test-asan   # AddressSanitizer + UndefinedBehaviorSanitizer 下重跑，13/13 通過
```

## 2. Benchmark（四情境 + 延遲分佈）

```bash
make run-bench   # 等同 ./bench，可覆寫參數：./bench [S1_iters] [S2_iters] [threads]
```

實際輸出：

```text
env: page 16384 B, 8 cores | timer overhead ~28 ns/pair

S1 assignment pattern: 10,000 ints = 40,000 B x 1000000 iters, single thread

  malloc     mean    185.9 ns | p50    125 | p99    292 | p99.9     458 | max   1585584 ns | checksum 165000000
  myMalloc   mean     40.0 ns | p50      0 | p99     42 | p99.9      83 | max    368834 ns | checksum 165000000
  speedup (mean) 4.6x | checksums identical | pool fallbacks 0 | one-time pool init 300 us (64 slots x 40000 B, pre-touched)

S2a large allocations: 262,144 B x 50000 iters

  malloc     mean    323.1 ns | p50    250 | p99    459 | p99.9   12041 | max     86583 ns | checksum 8250000
  myMalloc   mean     67.0 ns | p50     42 | p99     84 | p99.9     167 | max      6791 ns | checksum 8250000
  speedup (mean) 4.8x | checksums identical | pool fallbacks 0 | one-time pool init 182 us (8 slots x 262144 B, pre-touched)

S2b large allocations: 4 MB x 25000 iters (VM / zero-fill regime)

  malloc     mean   1909.1 ns | p50   1666 | p99   3583 | p99.9   35750 | max    865834 ns | checksum 4125000
  myMalloc   mean   1584.8 ns | p50   1417 | p99   2209 | p99.9   19959 | max    143750 ns | checksum 4125000
  speedup (mean) 1.2x | checksums identical | pool fallbacks 0 | one-time pool init 1396 us (4 slots x 4194304 B, pre-touched)

S3 fragmented heap: S1 rerun after salting (50000 mixed-size blocks, half freed)

  malloc     mean    132.9 ns | p50     84 | p99    209 | p99.9     333 | max    214375 ns | checksum 165000000
  myMalloc   mean     32.6 ns | p50      0 | p99     42 | p99.9      42 | max     38125 ns | checksum 165000000
  speedup (mean) 4.1x | checksums identical | pool fallbacks 0 | one-time pool init 233 us (64 slots x 40000 B, pre-touched)

S4 contention: 4 threads x 250000 iters x 40000 B (pool side: one private pool per thread)
  malloc     mean   2742.8 ns | p50   1208 | p99  17417 | p99.9   67209 | max   9018292 ns | checksum 165000000
  myMalloc   mean     33.0 ns | p50      0 | p99     42 | p99.9      42 | max     18416 ns | checksum 165000000
  checksums identical
```

## 跨環境實測（Linux／glibc）

同一套測試與 benchmark 在 GitHub Actions 的兩個 Linux 環境重跑：Ubuntu 24.04、glibc 2.39、gcc 13.3、4 vCPU、4 KB page——x86_64 與 arm64（Neoverse-N2）各一。**13 項測試（含 ASan + UBSan）在兩個環境全數通過**。Benchmark 對照（mean，括號內為 malloc → myMalloc）：

| 情境 | Apple M2（macOS） | Linux x86_64（glibc） | Linux arm64（glibc） |
|---|---|---|---|
| S1 40 KB 單執行緒 | 4.6×（186 → 40 ns） | 1.3×（80 → 61 ns） | 1.3×（89 → 70 ns） |
| S2a 256 KB | 4.8×（323 → 67） | 1.2×（425 → 348） | 1.2×（154 → 131） |
| S2b 4 MB | 1.2×（1,909 → 1,585） | 1.1×（7,398 → 7,008） | 1.0×（1,290 → 1,264） |
| S3 髒 heap 重跑 S1 | 4.1×（133 → 33） | **7.5×（448 → 60）** | **5.2×（362 → 69）** |
| S4 四執行緒 | **83×（2,743 → 33）** | 1.6×（154 → 99） | 1.5×（114 → 74） |

讀法：

- **哪個情境差距大，隨平台而異**。glibc 對重複同尺寸和多執行緒（per-thread arena）處理得好（S1 1.3×、S4 1.5–1.6×），但對 heap 歷史敏感——S3 撒過雜物之後，同一個 40 KB 迴圈的 malloc 從 80 ns 惡化到 448 ns（5.6 倍），pool 不動。macOS 相反：S3 影響小，S4 的共享 allocator 競爭嚴重。
- **三個環境的共同點是 myMalloc 的延遲分佈**：p99 全部落在 40–130 ns，跨情境、跨平台、跨 heap 狀態幾乎不動。
- 兩個 Linux 環境是共享雲主機（Azure VM），絕對值含虛擬化與鄰居雜訊，相對結構才是重點。pool 側絕對值比 M2 高，主因是頁大小：40 KB 在 4 KB page 上每次要觸 10 頁，16 KB page 上只要 3 頁。
- 這輪跨環境測試同時抓到一個真實的可移植性問題：glibc 在 `-std=c11` 嚴格模式下不暴露 `clock_gettime`／`CLOCK_MONOTONIC_RAW`（macOS 預設全開所以本機測不到），已修正（feature test macro + `CLOCK_MONOTONIC` fallback）。

## 讀數指南

- **p50 = 0 ns** 表示單次操作低於 Apple Silicon 時鐘的一個 tick（粒度約 42 ns）——pool 的 alloc+touch+free 整輪快於計時器能分辨的最小單位。
- **max 欄含 OS 排程搶佔雜訊**（兩側皆然），穩健的 tail 指標是 p99／p99.9。
- **checksums identical** 是 benchmark 內建的正確性交叉驗證：兩 backend 寫入並讀回的資料逐位一致。
- **pool fallbacks 0** 表示量測期間所有配置都走快路徑，無一退回系統 malloc。
- 毫秒數與 mean 會隨機器狀態浮動（malloc 側約 ±20%，pool 側穩定在 tick 粒度內）；各情境的**相對關係**與 pool 延遲分佈的恆定性為可重現的結論。

## Exit codes

`tests`：全過回 0，任一失敗回 1。`bench`：checksum 不一致回 1（正確性驗證失敗），正常回 0。可直接接 CI。
