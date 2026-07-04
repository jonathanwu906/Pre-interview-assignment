# 題目一：權限設計 — C# 實作與 Testing Results

設計說明（語意假設、演算法、複雜度與下界論證、否決方案、演進路線）見 [`../design-doc.md`](../design-doc.md)。本文件收錄執行方式與**實際執行輸出**。

環境：.NET 10（無任何外部套件相依，clone 後直接執行）。

## 1. 測試（design doc §6 的 13 項測資）

```bash
dotnet run
```

實際輸出：

```text
PASS  1 sample ACL (U5n, G7E33aR, U33, G333, U7a01 — all near-miss) → not readable
PASS  2 closure(U5nn) = 8 identities incl. G7E33a
PASS  3 G7E / G7E33a cross grants do not cover each other
PASS  4 direct user grant → readable
PASS  5 single-layer group grant → readable
PASS  6 10^4-deep chain → readable, no overflow (why the closure BFS is iterative)
PASS  7 diamond → closure deduplicated
PASS  8 cycle → terminates, readable via either group
PASS  9 self-loop → terminates
PASS  10 dangling-only ACL → not readable, no crash
PASS  11 empty ACL → not readable
PASS  12 groupless user (no index entry) granted directly → readable
PASS  13 mixed file set → readable subset in input order
13/13 tests passed
```

多數測試同時跑本設計與獨立的 naive 對照（`NaiveChecker`）並要求一致；少數只檢查閉包內容的測項（如 closure 集合、混合檔案序）僅驗本設計。

## 2. 題目範例（JSON 輸入）

```bash
dotnet run -- sample-input.json
```

實際輸出（U5nn 可讀的檔案，依輸入順序）：

```text
team-roadmap.md
personal-note.txt
ops-runbook.md
```

`sample-file.txt`（ACL 為題目的五條近似 ID：U5n、G7E33aR、U33、G333、U7a01）與 `hr-policy.pdf` 被正確排除——U5nn 的有效身分集合與其 ACL 交集為空。自訂輸入請依 `sample-input.json` 的格式：`user` + `groups`（各列 `members`、`subGroups`）+ `files`（各帶 `acl`）。

## 3. Benchmark（含 differential 驗證）

```bash
dotnet run -c Release -- benchmark 500 200 100000 2
```

參數 = 鏈數、深度、檔案數、每檔 ACL 條數。實際輸出：

```text
topology: 500 chains x depth 200 = 100,000 groups; 100,000 files x 2 ACL entries
naive (per-file downward DFS): 1,270 ms
design (index + closure + scan): 55 ms
speedup: 23.1x
readable: 402; outputs identical: True
```

毫秒數隨機器與當次執行浮動（多次實測 naive 約 1.2–1.4 s、本設計約 55–61 ms、約 23×）；`readable: 402` 與 `outputs identical: True` 是固定 seed（xorshift64\*, seed 42）下的**確定性結果**，任何環境每次執行皆相同。

## 跨環境實測（Linux）

同一套測試與 benchmark 在 GitHub Actions 的兩個 Linux 環境（Ubuntu 24.04、.NET 10、4 vCPU；x86_64 與 arm64 各一）重跑，**13/13 全數通過**：

| 環境 | naive | 本設計 | 倍數 | 可讀數 |
|---|---|---|---|---|
| Apple M2（macOS） | 1,270 ms | 55 ms | 23.1× | 402 |
| Linux x86_64 | 2,017 ms | 72 ms | 28.0× | 402 |
| Linux arm64 | 1,883 ms | 81 ms | 23.2× | 402 |

可讀清單在三個環境逐項一致（固定 seed 的確定性結果）；毫秒數隨硬體浮動，量級與相對關係一致。

## Exit codes

`0` 全部通過／執行成功；`1` 測試失敗或 benchmark 兩實作輸出不一致；`2` 輸入檔不存在或 JSON 格式錯誤。可直接接 CI。
