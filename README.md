# Pre-interview Assignment

三題解答，每題交付三件事：**source code**、**設計說明**、**testing results**。

| 題目 | 主題 | 設計說明 | 實作 | 測試結果 |
|---|---|---|---|---|
| Q1 | 權限設計（巢狀群組 × ACL 批次判定） | [design-doc.md](Q1/design-doc.md) | [csharp/](Q1/csharp/) | [csharp/README.md](Q1/csharp/README.md) |
| Q2 | myMalloc（反覆 malloc/free 的效能） | [design-doc.md](Q2/design-doc.md) | [c/](Q2/c/) | [c/README.md](Q2/c/README.md) |
| Q3 | 色塊矩陣找規律 | [design-doc.md](Q3/design-doc.md) | [csharp/](Q3/csharp/) | [csharp/README.md](Q3/csharp/README.md) |

另附 [quant-trading-connections.md](quant-trading-connections.md)：各題設計與量化交易基礎設施的結構對應。

三題的測試與 benchmark 另有 CI（[`.github/workflows/ci.yml`](.github/workflows/ci.yml)）在 Linux x86_64／arm64 兩個環境自動重跑。

## Q1 快速執行

環境：.NET 10，零外部套件相依，clone 後直接執行。

```bash
cd Q1/csharp
dotnet run                                            # 13 項測試
dotnet run -- sample-input.json                       # 題目範例：輸出可讀檔案清單
dotnet run -c Release -- benchmark 500 200 100000 2   # 10⁵ 群組 × 10⁵ 檔案，naive vs 本設計
```

重點結果：**13/13 測試通過**；10⁵ 規模下本設計 **55 ms** vs naive 約 1.3 s（約 **23×**；Linux x86_64／arm64 上 23–28×），naive 與本設計是兩份獨立實作、輸出逐項互驗一致。實際執行輸出與跨環境數據見 [Q1/csharp/README.md](Q1/csharp/README.md)，設計與否決方案見 [Q1/design-doc.md](Q1/design-doc.md)。

## Q2 快速執行

環境：POSIX + C11 編譯器（實測環境 Apple M2／Apple clang），零外部相依。

```bash
cd Q2/c
make test        # 13 項測試
make test-asan   # AddressSanitizer + UBSan 下重跑
make run-bench   # 四情境 benchmark（延遲分佈 + 內建 checksum 交叉驗證）
```

重點結果：**13/13 測試通過**（含 sanitizers，macOS 與兩個 Linux 環境皆過）。差距大的情境隨平台而異——macOS（M2）上四執行緒競爭快**數十倍**、題目原型 4–5×；Linux／glibc 上換成髒 heap 情境最痛（**5.2–7.5×**）、競爭只有 1.5×。三個環境的共同點：**myMalloc 的 p99 恆在 40–130 ns**，不隨情境、平台、heap 狀態變動。單機與跨環境數據見 [Q2/c/README.md](Q2/c/README.md)，設計思路見 [Q2/design-doc.md](Q2/design-doc.md)。

## Q3 快速執行

環境：.NET 10，零外部相依（含自製 PNG codec）。

```bash
cd Q3/csharp
dotnet run -- selftest        # 12 項驗證（codec / 抽色 round-trip / 分析不變量）
dotnet run                    # 完整分析（內建已數位化矩陣）
dotnet run -- <image.png>     # 對圖片重新抽色後分析
```

重點結果：**12/12 驗證通過**（macOS 與兩個 Linux 環境皆過，分析輸出逐字元相同）；主規則為上下左右相鄰不同色（隨機打亂 20,000 次僅出現 1 次），第 4/5 列重複、由星 1 標記；乍看顯著的欄位相似，改用守規則的隨機盤面重測後約 5% 會自己出現，判為規則副作用、不採納。發現與驗證見 [Q3/design-doc.md](Q3/design-doc.md)，實際輸出見 [Q3/csharp/README.md](Q3/csharp/README.md)。
