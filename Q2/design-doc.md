# 題目二：myMalloc — Design Doc

**結論**：反覆 malloc/free 慢，是因為每次呼叫都可能踩進 allocator 的慢路徑——進 kernel 要記憶體、首次寫入缺頁、內部結構搜尋、多執行緒搶鎖。最直接的解法是把配置搬出迴圈：先配一次、重複使用。當呼叫端必須保持 malloc/free 的形狀（大小是執行期算出來的、要逐次釋放），就把同一件事做成元件：**固定大小的 slot pool + free list**。啟動時一次 `mmap` 並把每一頁先寫過，之後 myMalloc／myFree 都只是幾個指標操作——不進 kernel、不缺頁、不搜尋、不加鎖。實測（Apple M2）：題目的 40 KB 迴圈平均快 4–5 倍；四執行緒同時配置時快數十倍（一次實測 83 倍、p99 差數百倍，此組競爭雜訊最大）；而且延遲接近常數——pool 的 p99 在 40 KB–256 KB 級距都是 42–84 ns（4 MB 大塊被逐頁寫入主宰、是唯一例外），系統 malloc 的 p99.9 則隨情境在 0.3–67 µs 之間跳動。也要說清楚：單執行緒、同尺寸、穩定重複是現代 allocator 的強項（4 MB 時差距只剩 1.2 倍），這個設計贏的是 allocator 繞不開成本的場合，以及任何場合下都穩定的 tail。

## 1. malloc 為什麼慢

每次呼叫可能付出的成本（機制通用；門檻數值因實作而異）：

| # | 成本 | 發生什麼事 | 本設計的處理 |
|---|---|---|---|
| 1 | 進 kernel＋缺頁 | 大塊配置直接找 OS 要（glibc 預設超過 128 KB 走 mmap/munmap，參考 6；macOS 實測大塊成本隨頁數線性成長）；拿到的頁是 lazy 的，首次寫每頁都缺頁 | 啟動時一次配好、每頁先寫過，之後不再進 kernel |
| 2 | 內部管理 | 維護 chunk header、在多層 bin／cache 之間找合適的塊、free 時嘗試合併鄰居 | 同尺寸 slot + free list：push/pop 各一次，不搜尋、不合併 |
| 3 | 碎片化 | 交錯配置釋放留下大小不一的洞，效能隨 heap 歷史劣化 | 同尺寸 slot 結構上不會有外部碎片；行為跟 heap 狀態無關（實測 S3） |
| 4 | 鎖競爭 | 多執行緒共用 allocator 狀態；小尺寸有 per-thread cache 擋著，中大尺寸還是會搶 | 一執行緒一個 pool，沒有共享就沒有鎖（實測 S4） |
| 5 | Cache／TLB | 每次拿到的位址不固定，資料散在 heap 上 | LIFO 把剛釋放、cache 還熱的 slot 優先發回去 |
| 6 | 延遲不穩 | 同一行 malloc 有時幾十 ns、有時幾十 µs；對延遲敏感的系統，尖峰比平均值要命 | 每次操作走的指令路徑固定，延遲自然穩定 |

測資設計要注意：題目那種「反覆配同一個大小」其實接近 allocator 的最佳情境——剛 free 的塊會被直接回收再用。所以 benchmark 兩邊都要量：最平坦的情境照實呈現差距不大，踩到痛點的情境（大塊、髒 heap、多執行緒）呈現差距的上限（§4）。

## 2. 改善的優先順序

依約束從強到弱：

1. **搬出迴圈**：迴圈外配一次、迴圈內重複用。改得動呼叫端就用這招，成本 1＋2 全消。前提：大小固定或抓得到上界。
2. **大小編譯期已知且不大 → stack 或 static**：最快的配置方式。本題大小是執行期算的，且 40 KB 放 stack 有點緊。
3. **生命週期成批 → region／arena**：一大塊裡 bump pointer 往前推，整批一次歸還。文獻裡自訂 allocator 唯一穩定大勝的類別（參考 2），代價是放棄逐次 free——跟本題的使用形狀不合。
4. **順序任意、要逐次回收 → 固定大小 pool（本設計）**：把第 1 招包成保留 malloc/free 介面的元件。
5. **都不符合 → 用現成的好 allocator**，不要自己寫通用的。

本題落點：大小執行期算出（`10000 * sizeof(int)`）、逐次釋放、介面是 malloc/free → 第 4 檔。

## 3. 設計

三個決定（`c/mymalloc.c`，約 160 行）：

1. **啟動時把 kernel 成本一次付清**。`my_pool_create` 一次 `mmap` 整個 pool 並 `memset` 寫過每一頁——缺頁全部發生在這裡，熱路徑上不會再出現（成本 1、6 一起消掉）。一次性成本實測約 80–120 µs/MB，長時間跑的程式攤下來趨近零。這是拿空間和啟動時間換執行中延遲，帳要算明白。
2. **free list 直接穿在空 slot 裡，LIFO**。空 slot 的前 8 bytes 就是下一個空 slot 的指標——不需要 header、不佔額外記憶體。alloc = pop、free = push，各是常數次比較加指標操作，**worst-case O(1)**，不是攤銷。LIFO 順便讓剛釋放、cache 還熱的 slot 下次優先發出去（成本 5）。
3. **超出範圍就透明退回 malloc**。請求超過 slot 大小、或 pool 用完時退回系統 malloc；`my_pool_free` 用一次位址範圍比較判斷這塊是誰的、各自歸還。所以介面是嚴格的 drop-in：任何 size 都正確，設定好的尺寸走快路徑。`fallback_count` 對外可查，池夠不夠大看它就知道。

API 分兩層：`my_pool_t` 可以開多個實例（多執行緒的用法就是一執行緒一個 pool，零共享）；`myMalloc`／`myFree` 是包在單一預設 pool 上的便捷層，對應題目的函式簽名。執行緒安全的約定明寫在 header：pool 不加鎖，跨執行緒共用屬於用錯——這是設計決定，不是漏做，見 §6。

**刻意不做的**：不做 per-slot header、不做多種尺寸、不做相鄰合併、不做 cache coloring。這套做法承襲 slab allocator（參考 1）「同型物件重複用、跳過通用路徑」的想法；slab 其餘機制服務的是 kernel 場景（多型物件、cache set 分佈），本題的工作型態（單一熱尺寸、一次一塊）用不到，加了只是複雜度。對照 TLSF（參考 3）：它為了對**任意**大小做到 O(1) 需要兩層 bitmap 索引，這裡把大小固定成一種，O(1) 自然成立——複雜度預算花在題目真正需要的地方。

空間代價：pool 預留 slot 大小 × 數量（本題預設 40,000 B × 64 ≈ 2.6 MB），不管實際用多少；對齊 16 bytes 的內部浪費每 slot 最多 15 B。

## 4. Benchmark 與實測

量法：同一個 harness、兩個 backend（系統 malloc vs myMalloc），透過 volatile 函式指標呼叫避免編譯器把配置整段摺掉；每次操作 = 配置 + 每頁寫一個 byte + 釋放，讓缺頁（如果有）落在計時範圍內——真實程式拿到記憶體本來就是要寫的；每個情境先暖身再量；固定 seed。兩個 backend 寫入資料的 checksum 必須一致，benchmark 內建這個正確性檢查。讀數注意：Apple Silicon 的時鐘粒度約 42 ns，p50 = 0 表示單次操作快過一個 tick；`max` 欄含 OS 排程搶佔的雜訊（兩邊都有），看 tail 要看 p99／p99.9（tail 對延遲敏感系統比平均值致命，參考 4）。測資是合成的、和真實使用本有落差，所以兩面都測：S1 是最平坦的誠實基準，S2–S4 專踩痛點。

環境：Apple M2（8 核、16 KB page）、macOS 26.5.1、Apple clang 21、`-O2`。實測（`make run-bench`，完整輸出見 `c/README.md`）：

| 情境 | malloc mean / p99 | myMalloc mean / p99 | mean 差 |
|---|---|---|---|
| S1 題目原型：40 KB × 10⁶ 次，單執行緒 | 186 ns / 292 ns | 40 ns / 42 ns | **4.6×** |
| S2a 大塊 256 KB × 5×10⁴ | 323 ns / 459 ns | 67 ns / 84 ns | **4.8×** |
| S2b 大塊 4 MB × 2.5×10⁴ | 1,909 ns / 3,583 ns | 1,585 ns / 2,209 ns | **1.2×** |
| S3 髒 heap 上重跑 S1 | 133 ns / 209 ns | 33 ns / 42 ns | **4.1×** |
| S4 四執行緒同時配置（pool 側一執行緒一個 pool） | 2,743 ns / 17,417 ns | 33 ns / 42 ns | **83×**（p99 414×） |

逐情境：

- **S1**：macOS 對重複同尺寸處理得不差（186 ns），4.6 倍主要是內部管理的差。tail 已經分開：malloc p99.9 = 458 ns、max 到 ms 級；pool p99.9 = 83 ns。
- **S2**：256 KB 時 macOS 還有快取（4.8 倍，但 malloc p99.9 = 12 µs，開始看到 VM 抖動）；**4 MB 時 macOS 的大塊快取把 VM 成本吸掉大半，兩邊都被逐頁寫入的時間主宰，只差 1.2 倍**——同尺寸穩態確實難大勝，照實寫。大塊配置一旦沒被快取接住，成本會隨頁數線性成長，而什麼時候踩進這條慢路徑並不好預測——延遲穩定性的主張就是針對這個。
- **S3**：先塞 50,000 個混合大小的塊、釋放一半，再重跑 S1——malloc 的平均動了約 30%（這一輪變快，換一輪可能變慢，方向說不準），pool 的數字在時鐘粒度內不動。allocator 行為會不會跟著 heap 歷史漂，量出來很清楚。
- **S4**：共享 allocator 在四執行緒下平均膨脹十幾倍、p99 到 17 µs；pool 側因為零共享完全不動（p99 = 42 ns，跟單執行緒一樣）。這組競爭雜訊最大，倍數在數十倍間浮動（表中是其中一次），不變的是 pool 側 p99。pool 側一執行緒一個 pool 不是取巧，就是這個設計對多執行緒的用法。

**跨環境**：同一套 benchmark 在 Ubuntu 24.04（glibc 2.39，x86_64 與 arm64 各一）重跑，結構不一樣：glibc 對重複同尺寸與多執行緒處理得更好（S1 1.3×、S4 1.5–1.6×），但對 heap 歷史明顯敏感——髒 heap 下 malloc 從 80 ns 惡化到 448 ns，S3 的差距拉到 5.2–7.5×。三個環境的共同點是 pool 的延遲分佈：p99 全部在 40–130 ns、不受 heap 狀態影響。哪個成本咬人依平台而異，行為是常數這件事不隨平台變。逐項數字見 `c/README.md`。

## 5. 否決的做法

| 做法 | 問題 |
|---|---|
| 自製通用 allocator（多尺寸 + 搜尋） | 文獻結論很清楚（參考 2）：多數自訂 allocator 打不贏成熟的通用 allocator。我接受這個前提，只在通用路徑有繞不開成本的場合（kernel 邊界、鎖、heap 歷史）宣稱贏 |
| Region／arena | 文獻裡唯一穩定大勝的類別，但要放棄逐次 free，跟題目的使用形狀不合；混合式（region + 逐次釋放）對本題是多餘的複雜度 |
| 搬出迴圈（不算否決） | 這是第一正解，改得動呼叫端就該先用；本設計是它在「呼叫端形狀不能改」時的等價物，兩者不衝突 |
| 共享的 lock-free pool | 本題沒有跨執行緒 free 的需求；CAS 熱路徑比 per-thread pool 的無同步路徑慢也複雜。列入 §6 |

## 6. 之後怎麼演進

出現跨執行緒 free 時：per-thread pool 前面加一層批次歸還（Bonwick & Adams 2001 的 magazine 做法）或 MPSC 無鎖佇列。熱尺寸變多時：開幾個獨立 pool 按大小路由，仍然不做通用化。更進一步壓 TLB：改用大頁。維運上把 `fallback_count` 接進監控——池用完對延遲敏感的系統是容量規劃出錯的前兆，不是正常降級。熱路徑零配置是低延遲交易系統的通行做法（參考 5），延遲穩定性同理關鍵；與交易系統的完整對應整理在 repo 根目錄的 `quant-trading-connections.md`。

## 7. 測試設計

13 項（`c/tests.c`），涵蓋正確性、路由、邊界；另外用 AddressSanitizer + UndefinedBehaviorSanitizer 全部重跑一次，也全過。

| # | 測項 | 驗證目標 |
|---|---|---|
| 1 | 題目原型：10,000 個 int 寫滿驗和 | drop-in 基本路徑、16-byte 對齊 |
| 2 | 排空 pool：slot 對齊、互不重疊、間距一致 | 記憶體佈局 |
| 3 | 全部 slot 同時寫滿全長 | 無重疊、無越界 |
| 4 | alloc–free–alloc 拿回同一個 slot | LIFO 回收 |
| 5 | 用完 → 透明退回 malloc，釋放各歸各的 | 容量邊界 |
| 6 | 超過 slot 尺寸 → 退回 malloc、pool 不受影響 | 尺寸邊界 |
| 7 | free(NULL) 兩層 API 都是 no-op | 邊界 |
| 8 | size 0 來回一趟 | 邊界 |
| 9 | 隨機 alloc/free 壓力 + 內容 tag 驗證、pool 復原 | 混合路由下不互踩 |
| 10 | 同一串隨機操作分別餵 pool 和系統 malloc，行為一致 | 跟參考實作對答案 |
| 11 | 歸屬判斷：跨 pool、外來指標 | 路由判準 |
| 12 | 建構參數邊界：0、溢位、1-byte slot | 防禦性 |
| 13 | 生命週期：init 一次、lazy 預設、shutdown 重來 | API 約定 |

Benchmark 另有一層檢查：每個情境兩個 backend 的資料 checksum 必須一致（§4 表格全部 identical）。

## 附錄：重現方式

於 `c/` 下：`make test`（13/13）；`make test-asan`（sanitizer 下 13/13）；`make run-bench`（五個情境，參數可改：`./bench [S1_iters] [S2_iters] [threads]`）。環境需求：任何 POSIX 系統 + C11 編譯器，零外部相依。實際輸出收錄於 `c/README.md`。

## 參考資源

1. Bonwick, J., *[The Slab Allocator: An Object-Caching Kernel Memory Allocator](https://www.usenix.org/conference/usenix-summer-1994-technical-conference/slab-allocator-object-caching-kernel)*, USENIX Summer 1994。「同型物件重複用」的出處；§3 說明本題為何不需要它其餘的機制。
2. Berger, E., Zorn, B. & McKinley, K., *[Reconsidering Custom Memory Allocation](https://people.cs.umass.edu/~emery/pubs/berger-oopsla2002.pdf)*, OOPSLA 2002。實測多數自訂 allocator 不比成熟通用 allocator 快，穩定例外是 region；本文接受這個前提並據此設限（§5）。它沒量 tail latency，延遲穩定性的部分由 3、4 補。
3. Masmano, M. et al., *[TLSF: A New Dynamic Memory Allocator for Real-Time Systems](http://www.gii.upv.es/tlsf/files/papers/ecrts04_tlsf.pdf)*, ECRTS 2004。任意尺寸下 O(1) worst-case 的參考點（§3 的對照）。
4. Dean, J. & Barroso, L.A., *[The Tail at Scale](https://www.barroso.org/publications/TheTailAtScale.pdf)*, CACM 2013。為什麼 p99 比平均值要命；§4 用延遲分佈而不是單一平均值報告的依據。
5. Cook, C., *[When a Microsecond Is an Eternity: High Performance Trading Systems in C++](https://www.youtube.com/watch?v=NH1Tta7purM)*, CppCon 2017。低延遲交易系統「熱路徑零配置」的業界做法。
6. glibc wiki, *[MallocInternals](https://sourceware.org/glibc/wiki/MallocInternals)*。§1 表中 glibc 側機制（bins、mmap 門檻、tcache）的出處。
