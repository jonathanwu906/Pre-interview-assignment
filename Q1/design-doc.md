# 題目一：權限設計 — Design Doc

**結論**：先把成員關係反向索引一次，從目標使用者沿「子群組 → 父群組」方向走出他所屬的全部群組（迭代 BFS），得到有效身分集合 S；之後每個檔案的每條 ACL entry 只需要對 S 查一次 hash。單次查詢 O(E + A)（E = 成員關係總數、A = ACL entry 總數），而最壞情況下每條資料至少得讀一次（§3），所以這已經是下限的量級。實測 10⁵ 群組、10⁵ 檔案：逐檔展開的 naive 做法約 1.3 秒，本設計 55 毫秒，差約 23 倍，兩種做法輸出逐項一致。題目範例的答案：**U5nn 讀不到該檔案**——ACL 五條 entry 都只是長得像的 ID，沒有一條真的等於他的有效身分。

## 1. 語意假設

動手前先把題目沒講死的地方定下來。這五條都寫進了程式註解和測試：

1. **ID 精確比對**。ID 是不透明、區分大小寫的字串。範例 ACL 裡三組近似 ID（U5n／U5nn、G7E33aR／G7E33a、G333／G33）看起來就是故意設的——前綴或子字串比對會放錯權限。實作全面用 `StringComparer.Ordinal`，並排除 culture 相關比較。
2. **成員資格向上傳遞**。U5nn ∈ G17yT、G17yT 是 G7E33a 的 sub-group，則 U5nn 是 G7E33a 的有效成員。題目沒明說，我採用這個解讀並固定下來。
3. **群組圖不假設是樹**。共用 sub-group（菱形）、互相包含（環）都不能影響正確性，也不能讓程式跑不完。
4. **容忍不存在的 ID**。ACL 引用沒定義過的 user／group 時，那條 entry 永遠不匹配，不報錯。
5. **查詢型態是一次性批次**：單一使用者對全量檔案。假設變了怎麼辦見 §5。

## 2. 設計

關鍵觀察：資料儲存的方向跟查詢需要的方向相反。原始資料是 parent → child（每個群組列出它的 sub-groups），但「U5nn 屬於哪些群組」得從 child 往 parent 走。所以分三步：

1. **建反向索引**（一次，O(E)）：`user → 直屬群組`、`子群組 → 父群組` 兩張 hash table。
2. **算出 S**：從 U5nn 的直屬群組出發，沿反向邊 BFS。S 本身就當 visited set 用，菱形和環都只會處理一次。範例中 S = {U5nn, G33, G7E, G54A, G17yT, G6f4D9, G8ig5, G7E33a}，共 8 個身分。
3. **線性掃檔案**：每條 ACL entry 對 S 查一次，命中任一條即可讀。

範例逐條比對：U5n（≠ U5nn）、G7E33aR（≠ G7E33a，也 ≠ G7E）、U33、G333（≠ G33）、U7a01——沒有一條在 S 裡，所以不可讀。

實作細節：BFS 用迴圈不用遞迴，10⁴ 層深的鏈也不會 stack overflow（有測試）；S 是 Ordinal 的 `HashSet<string>`，查一次是攤銷 O(1)；掃描階段唯一要留在記憶體的就是 S，檔案來源可以是 stream。

## 3. 複雜度：為什麼快不過這個量級

單次查詢 O(E + A)：建索引 O(E)、閉包只走該使用者可達的子圖、掃描每條 entry O(1)。

為什麼沒有演算法能更快：假設某個演算法沒讀完所有 ACL entry 就下結論，那把它沒讀的那條換成 U5nn 本人——答案翻面，但演算法讀過的內容一個字沒變，它會給出同樣的答案，也就錯了。成員關係同理：沒讀的那筆可能正好是唯一把使用者接到授權群組的一段。所以最壞情況下每條 entry、每筆關係至少要讀一次，Ω(E + A) 躲不掉，本設計就在這個量級。

規模行為：索引記憶體 O(E)；掃描階段常駐的只有 S（KB 等級），N 個檔案可以串流處理，也可以按檔案切給多台機器、每台拿一份 S 平行掃，彼此不用溝通。

實測（固定 seed，可重現）：500 條鏈 × 深度 200 = 10⁵ 群組，10⁵ 檔案、每檔 2 條 ACL entry。逐檔向下展開的 naive 實作 1,270 ms；本設計 55 ms，**23 倍**；兩實作輸出逐項一致（402 檔可讀）。環境：.NET 10。同一套 benchmark 在 Linux（x86_64／arm64）重跑為 23–28 倍、可讀清單逐項相同，見 `csharp/README.md`。

## 4. 否決的做法

| 做法 | 問題 |
|---|---|
| 逐檔向下 DFS（不做前處理） | 同一個子圖對每個檔案重複展開，實測慢 22 倍；想加快取的話，有環的圖上快取失效很難做對 |
| 預先算好全部群組 × 群組的可達性 | O(M²) 空間放不下；群組一動就要大範圍重算。本題只查一個使用者，用不到全對全 |

本設計介於兩者之間：只攤平這個使用者可達的部分。攤到什麼程度應該由查詢模式決定，見下節。

## 5. 假設變了怎麼辦

**同一批 ACL 被反覆查詢（不同使用者、高頻）**：把攤平再往前移——離線維護 principal → 檔案清單的反向索引，查詢變成把 S 中各 principal 的清單取聯集，不再碰 N。Google Zanzibar 對深層巢狀群組就是走這條路（Leopard index，參考 1），在兆級 ACL、每秒百萬次檢查的規模上驗證過。

**群組異動變頻繁**：對使用者閉包做快取、異動時失效。這時「權限變更多久生效」要有明確上界——過期的授權或撤銷在合規場景就是事故，失效策略得跟稽核要求一起設計。

這一題與交易系統的對應（pre-trade 檢查、市場資料 entitlement、限額階層）整理在 repo 根目錄的 `quant-trading-connections.md`。

## 6. 測試設計

測資自己設計，涵蓋語意、結構、規模三類；C# 實作 13 項全數通過。

| # | 測項 | 期望 | 驗證目標 |
|---|---|---|---|
| 1 | 題目範例（五 entry 皆近似 ID） | 不可讀 | 精確比對語意 |
| 2 | U5nn 閉包 = 8 個身分、含 G7E33a | — | 向上傳遞 |
| 3 | G7E 與 G7E33a 交叉授權 | 互不覆蓋 | 近似 ID 雙向驗證 |
| 4 | ACL 直接列 user | 可讀 | 基本路徑 |
| 5 | 單層 group 授權 | 可讀 | 基本路徑 |
| 6 | 10⁴ 層巢狀鏈 | 可讀、不溢位 | 迴圈式 BFS 的必要性 |
| 7 | 菱形（雙路徑同祖先） | 閉包去重 | visited set 正確性 |
| 8 | 環（互為 sub-group） | 跑得完且正確 | 不依賴樹狀假設 |
| 9 | 自己包含自己 | 跑得完 | 邊界 |
| 10 | ACL 只含不存在的 ID | 不可讀、不當機 | 容錯 |
| 11 | 空 ACL | 不可讀 | 邊界 |
| 12 | 不屬於任何群組的 user 直接授權 | 可讀 | 索引缺項容錯 |
| 13 | 混合檔案集 | 恰為可讀子集、保持輸入序 | end-to-end |

規模上的正確性驗證：naive 和本設計是分開寫的兩份實作，benchmark 在固定 seed 的隨機資料上要求兩者輸出逐項一致，10⁵ 檔案下完全相同。

## 附錄：重現方式

於 `csharp/` 下：`dotnet run`（13 項測試，13/13）；`dotnet run -- sample-input.json`（輸出可讀清單，正確排除範例檔案）；`dotnet run -c Release -- benchmark 500 200 100000 2`（含 naive 對照與輸出互驗）。環境：.NET 10。三個指令的實際執行輸出收錄於 `csharp/README.md`。

## 參考資源

以下為延伸閱讀與演進方向的參考，非本題演算法的來源——核心的反向索引 + 單源閉包是標準圖論做法。

1. Pang, R. et al., *[Zanzibar: Google's Consistent, Global Authorization System](https://www.usenix.org/conference/atc19/presentation/pang)*, USENIX ATC '19（[論文 PDF](https://www.usenix.org/system/files/atc19-pang.pdf)）。
   §5 攤平策略（Leopard index）與規模數據的出處。演講影片：[完整議程演講](https://www.youtube.com/watch?v=mstZT431AeQ)、[lightning talk](https://www.youtube.com/watch?v=cJ334qJ0jBI)。
2. AuthZed, *[Learn Google Zanzibar — Core Concepts and Architecture](https://authzed.com/learn/google-zanzibar#core-concepts-and-architecture)*.
   ReBAC 概念與 Zanzibar 架構的導讀（relation tuples、userset rewrite）。
3. [zanzibar.academy](https://www.zanzibar.academy/) — AuthZed 的逐段註解版論文，方便定位資料模型與 Leopard 章節。
