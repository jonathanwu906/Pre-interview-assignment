# 題目三：色塊矩陣 — C# 實作與 Testing Results

設計說明（做法、發現、驗證設計）見 [`../design-doc.md`](../design-doc.md)。本文件收錄執行方式與**實際執行輸出**。

環境：.NET 10，零外部相依（含自製 PNG codec，不需要任何影像套件）。

## 1. 驗證（12 項 selftest）

```bash
dotnet run -- selftest
```

實際輸出：

```text
PASS  1 PNG round trip (filter 0)
PASS  2 PNG round trip (filters 0-4)
PASS  3 extractor recovers all 50 cell colors from synthetic image
PASS  4 extractor recovers both star positions
PASS  5 color counts R11 Y10 G15 B14, chi2=1.36 → uniform not rejected
PASS  6 horizontal adjacent same-color = 0/40
PASS  7 vertical violations = exactly the row4/5 seam (5 cells)
PASS  8 diagonal control = 14/72 (constraint is orthogonal-only)
PASS  9 near rows {(1,6,1),(4,5,0),(7,9,1)}, near cols {(2,4,2)}
PASS  10 null A: all three observed features significant (p < 0.02)
PASS  11 null B: col2~col4 marginal (p 0.03-0.09), near-rows explained (p 0.30-0.45)
PASS  12 battery sizes {19,26,20,2,23}; no property uniquely selects the stars
12/12 tests passed
```

第 3–4 項是抽色器的 round-trip：程式**自行合成**一張已知答案的網格圖（PNG），再用正式抽色管線讀回，必須還原出全部 50 格顏色與兩顆星的位置。

## 2. 完整分析

```bash
dotnet run                    # 內建已數位化的矩陣
dotnet run -- <image.png>     # 對圖片重新抽色後分析（支援 8-bit gray/RGB/palette/RGBA PNG）
```

實際輸出：

```text
Grid:
  R Y G B Y
  G B Y R G
  B R B G B
  G Y R Y R
  R G B G B
  R G B G B
  G B Y R Y
  Y R G Y G
  B G B R B
  Y R G B G
Stars: [(5, 3), (7, 1)] 

[1] 顏色分布 {'R': 11, 'Y': 10, 'G': 15, 'B': 14}  chi2=1.36 (df=3, 5%臨界=7.81) -> 與均勻分布無顯著差異
[2] 相鄰同色  水平 0/40  垂直 5/45 位置=[(4, 0), (4, 1), (4, 2), (4, 3), (4, 4)]  對角 14/72(對照)
[3] 近似列: [(1, 6, 1), (4, 5, 0), (7, 9, 1)]
    近似欄: [(2, 4, 2)]
[4] Null A  P(水平0同色)=0.00005  P(相鄰列全同)=0.00685  P(欄位對>=8/10相同)=0.00325
[5] Null B(條件化)  P(欄位對Hamming<=2)=0.0520 <-邊際  P(>=2對近似列)=0.3648 <-不顯著
[6] 星星掃描:
    與8鄰居皆不同: 共19格, 星星命中 [(7, 1)]
    與對角鄰皆不同: 共26格, 星星命中 [(5, 3), (7, 1)]
    列內唯一該色: 共20格, 星星命中 [(7, 1)]
    欄內唯一該色: 共2格, 星星命中 []
    被鄰居唯一決定: 共23格, 星星命中 [(5, 3), (7, 1)]
    結論: 無任何性質唯一挑出兩顆星 -> 星1壓在複製列縫線上(標記異常), 星2疑似干擾項
```

## 重現性

隨機比對固定 seed（xorshift64\*，seed 42）：任何機器、任何次執行的輸出——包含全部機率估計——逐字元相同。此主張已實測：同一程式在 GitHub Actions 的 Ubuntu 24.04 x86_64 與 arm64 環境重跑，**12/12 驗證通過，分析輸出與 macOS 本機逐字元一致**。

## Exit codes

`selftest`：全過回 0，任一失敗回 1。分析模式：正常回 0，輸入檔不存在或格式不支援回 2。
