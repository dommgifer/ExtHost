# ExtHost

以 WebView2（Edge / Chromium 核心）為基礎的瀏覽器，重點是**可以載入自己開發的未封裝擴充功能**，拿來改造公司內部系統。

## 執行需求

- Windows 10 / 11（x64）
- Microsoft Edge WebView2 Runtime：Windows 11 與大多數 Windows 10 已內建；沒有的話程式啟動時會提示下載
- **不需要**安裝 .NET，`ExtHost.exe` 是獨立執行檔

直接雙擊 `ExtHost.exe` 即可。第一次啟動時，Windows SmartScreen 可能因為執行檔沒有程式碼簽章而跳出警告。

## 資料存放位置

| 項目 | 位置 |
|---|---|
| 設定檔、分頁紀錄、已載入的擴充功能清單 | `%LOCALAPPDATA%\ExtHost\settings.json` |
| 書籤 | `%LOCALAPPDATA%\ExtHost\Bookmarks.json`（格式與 Chrome 的 Bookmarks 檔相同） |
| 書籤列的網站圖示快取 | `%LOCALAPPDATA%\ExtHost\Favicons\` |
| Cookie、快取、擴充功能的 storage | `%LOCALAPPDATA%\ExtHost\UserData\` |
| 錯誤紀錄 | `%LOCALAPPDATA%\ExtHost\exthost.log` |

**可攜模式**：在 `ExtHost.exe` 旁邊建立一個名為 `portable` 的空檔案（沒有副檔名），資料就會改存到執行檔旁的 `data\` 資料夾。

## 擴充功能

### 載入

1. 右上角選單 → 擴充功能管理（或按 `Ctrl+Shift+E`）
2. 按「載入未封裝的擴充功能」，選擇含有 `manifest.json` 的資料夾；也可以直接把資料夾拖進管理頁

ExtHost 只會記住資料夾路徑，不會複製檔案。移除擴充功能也不會刪除你的原始碼。

`samples\page-helper-demo` 是一個範例擴充功能，可以用來確認各項功能正常：

- content script 注入浮動面板
- `chrome.storage`
- 和背景 service worker 互傳訊息
- popup
- 選項頁
- 同源 `fetch` 會自動帶上登入 Cookie

### 開發流程

- 開啟「開發人員模式」（預設開啟）後，工具列會出現「重載擴充」按鈕（`Ctrl+Shift+R`）
- 開啟「監看資料夾變更」（預設開啟）後，儲存檔案約 1 秒內會自動重新載入擴充功能，並重新整理目前分頁
- 分頁的 DevTools：`F12`
- popup 的 DevTools：popup 頂端的 `</>` 按鈕（會自動釘選 popup，不會因為失去焦點而關閉）
- manifest 格式錯誤時，管理頁的卡片會顯示錯誤行號；修正存檔後會自動重試

### WebView2 的限制

WebView2 只提供擴充功能的執行環境，沒有 Chrome 的瀏覽器介面：

- **popup**：由 ExtHost 工具列代為開啟，需要在 manifest 設定 `action.default_popup`
- **沒有 popup 的 `action.onClicked`**：無法觸發，點工具列按鈕只會出現選單
- **側邊欄（Side Panel）**：WebView2 沒有側邊欄，ExtHost 會讀取 manifest 的 `side_panel.default_path`，點工具列按鈕時在主視窗右側開啟（可拖曳調整寬度，切換分頁時保持開啟）。沒寫 `default_path` 的擴充功能會嘗試找 `sidepanel.html` 等常見檔名。`chrome.sidePanel.open()`、`setOptions()` 等 API 可能無效
- **`chrome.tabs.query()` 的「目前分頁」**：WebView2 把每個 WebView2 當成獨立視窗，popup 和側邊欄用 `{ active: true, currentWindow: true }` 查詢時會拿到自己。ExtHost 會在 popup 和側邊欄裡改寫這類查詢（含 `lastFocusedWindow`），改回傳網址等於 ExtHost 目前分頁的那個分頁（分頁 id 是真的，可以接著用 `chrome.scripting.executeScript`）。兩個分頁網址完全相同時可能挑錯；background service worker 裡的查詢不受影響
- **依賴瀏覽器介面的 API**（`contextMenus`、`sidePanel`、`commands`、`tabs` 的部分功能等）：可能無法使用。管理頁會把這類權限標成橘色「需實測」，但這只是依權限名稱標示，實際能不能用還是要測
- `window.close()`：比照 Chrome，只有「由腳本開啟的分頁」或「歷史紀錄只有一筆的分頁」會被關閉，其他情況會忽略並在 DevTools console 印出警告。網頁關閉最後一個分頁時，ExtHost 會先補開一個新分頁，不會整個程式關掉
- `chrome.runtime.openOptionsPage()` 可能無效，建議改用 `window.open(chrome.runtime.getURL('options.html'))`，ExtHost 會開成新分頁
- **重新載入方式**：預設是「移除後重新加入」，一定會讀到新檔案，但**該擴充功能的 `chrome.storage` 資料可能被清除**。如果要保留資料，把 `settings.json` 的 `ReloadMode` 改成 `"toggle"`（停用再啟用），但部分修改可能不會生效

建議把擴充功能設計成以 content script + storage + runtime 訊息 + fetch 為核心、UI 直接注入頁面。這樣的擴充功能在 ExtHost 和一般 Chrome 都能跑。

## 書籤

操作方式與 Chrome 相同：

- **加入書籤**：按網址列右側的星號，或按 `Ctrl+D`。按下後就已經加入，跳出的小視窗可以改名稱、換資料夾、按「移除」刪除，或按「更多…」開啟完整編輯視窗（可改網址）。已加入書籤的頁面，星號會變成藍色實心
- **書籤列**：`Ctrl+Shift+B` 切換「一律顯示」。沒有開啟時，書籤列只會在新分頁出現（與 Chrome 相同）
  - 左鍵：在目前分頁開啟；中鍵或 `Ctrl`+左鍵：在背景新分頁開啟；`Ctrl+Shift`+左鍵：在新分頁開啟並切換過去
  - 資料夾點一下展開下拉選單；選單開著時，滑鼠移到另一個資料夾會直接切換
  - 放不下的書籤收在右側的 `»` 選單；「其他書籤」有內容時會顯示在最右邊
  - 右鍵選單：在新分頁中開啟、全部開啟、編輯、重新命名、複製、刪除、新增網頁、新增資料夾、書籤管理員、顯示書籤列
  - **拖曳**：書籤可以拖曳排序；拖到資料夾中間放進資料夾；拖到「其他書籤」放進其他書籤。也可以把分頁、網址列左側的網站圖示，或網頁裡的連結拖到書籤列上加入書籤
- **將所有分頁加入書籤**：`Ctrl+Shift+D`，把目前所有分頁存成一個書籤資料夾
- **書籤管理員**：`Ctrl+Shift+O`（或在網址列輸入 `chrome://bookmarks`）
  - 左側資料夾樹、右側書籤清單；上方搜尋框會搜尋所有書籤的名稱和網址
  - `Ctrl` / `Shift` 多選，選取後上方會出現「已選取 N 個項目」工具列
  - 拖曳排序，或拖到左側資料夾 / 清單中的資料夾來移動，可一次拖曳多個項目
  - `Delete` 刪除，刪除後左下角會出現「復原」，也可以按 `Ctrl+Z` 復原
  - `Enter` 或按兩下開啟（資料夾則進入資料夾）、`Ctrl+C` 複製網址、`Ctrl+F` 搜尋
  - 右上角「⋮」：新增書籤、新增資料夾、依名稱排序、匯入書籤、匯出書籤
- **匯出書籤**：選單 → 書籤 → 匯出書籤，產生的 HTML 檔與 Chrome 匯出的格式相同，可以匯入回 Chrome、Edge 或 Firefox
- **網站圖示**：開過的網站會記住它的圖示；沒開過的網站顯示預設的地球圖示

### 從 Chrome / Edge 匯入書籤

ExtHost 的書籤是獨立的，不會修改 Chrome 或 Edge 的資料。要把 Chrome 的書籤帶過來，請用「匯入」：

1. 右上角選單 → 書籤 → 匯入書籤和設定（書籤列是空的時候，也可以直接點書籤列上的「立即匯入書籤…」）
2. 在「來源」選擇要匯入的瀏覽器設定檔。ExtHost 會自動找出這台電腦上 Chrome、Edge（含 Beta / Dev / Canary）、Chromium、Brave 的所有設定檔，並顯示你在瀏覽器裡取的設定檔名稱
3. 按「匯入」

匯入規則與 Chrome 相同：

- 書籤列還是空的：匯入的書籤列項目直接放進書籤列，其他書籤放進「其他書籤」
- 書籤列已經有書籤：全部放進書籤列上新的「從 Google Chrome 匯入」資料夾，不會打亂原本的書籤
- 重複匯入不會去除重複項目

**找不到設定檔時**，在來源選「手動指定 Chrome / Edge 設定檔…」：

1. 在 Chrome 網址列輸入 `chrome://version`（Edge 請輸入 `edge://version`）
2. 找到「設定檔路徑」這一行，複製整行路徑
3. 貼到欄位中；也可以按「瀏覽…」直接選擇該資料夾裡的 `Bookmarks` 檔案

**書籤 HTML 檔**：Chrome、Edge、Firefox 的「匯出書籤」產生的 `.html` 檔也可以匯入，適合從別台電腦搬書籤。

**書籤檔損壞時**：啟動時如果 `Bookmarks.json` 讀取失敗，ExtHost 會先備份成 `Bookmarks.json.broken-日期時間`，並停止寫入書籤（避免空白內容覆寫原檔），同時提示復原方式。儲存書籤失敗時（例如磁碟空間不足），這次的修改會還原並顯示錯誤，不會出現「畫面上有、重開就不見」的情況。

Chrome 開著的時候也可以匯入。新版 Chrome 登入帳號後，部分書籤會存在 `Account Bookmarks` 檔，ExtHost 會和 `Bookmarks` 一起匯入。

## 快捷鍵

| 按鍵 | 功能 |
|---|---|
| `Ctrl+T` / `Ctrl+W` | 開新分頁 / 關閉分頁 |
| `Ctrl+Tab`、`Ctrl+Shift+Tab`、`Ctrl+1`～`9` | 切換分頁 |
| `Ctrl+L`、`Alt+D`、`F6` | 跳到網址列 |
| `F5`、`Ctrl+R` | 重新整理 |
| `Alt+←` / `Alt+→` | 上一頁 / 下一頁 |
| `F12`、`Ctrl+Shift+I` | 開發人員工具 |
| `Ctrl+Shift+E` | 擴充功能管理 |
| `Ctrl+Shift+R` | 重新載入所有擴充功能 |
| `Ctrl+D` | 將這個分頁加入書籤 / 編輯書籤 |
| `Ctrl+Shift+B` | 顯示 / 隱藏書籤列 |
| `Ctrl+Shift+O` | 書籤管理員 |
| `Ctrl+Shift+D` | 將所有分頁加入書籤 |
| 滑鼠中鍵點分頁 | 關閉分頁 |

## settings.json 欄位

| 欄位 | 預設值 | 說明 |
|---|---|---|
| `HomePage` | `about:blank` | 新分頁開啟的網址 |
| `SearchUrl` | Google | 網址列輸入關鍵字時使用，`{0}` 代表關鍵字 |
| `DevMode` | `true` | 開發人員模式 |
| `WatchExtensionFolders` | `true` | 檔案變更時自動重新載入擴充功能 |
| `ReloadTabAfterExtensionReload` | `true` | 重新載入擴充功能後，重新整理目前分頁 |
| `ReloadMode` | `reinstall` | `reinstall` 或 `toggle`（見上方「重新載入方式」） |
| `RestoreSession` | `true` | 啟動時還原上次的分頁 |
| `ShowBookmarkBar` | `false` | 一律顯示書籤列（關閉時只在新分頁顯示） |
| `LastBookmarkFolderId` | 無 | 上次用星號加入書籤時選的資料夾 |

要修改設定，可以從右上角選單 →「編輯 settings.json」開啟。**請先關閉 ExtHost 再改**，因為程式關閉時會覆寫這個檔案。

## 下載

- **正式版**：到 repo 的 [Releases](../../releases) 頁面下載 `ExtHost-版本.zip`，解壓縮後執行 `ExtHost.exe`
- **測試版**：每次合併到 `main` 都會自動編譯一次。到 [Actions](../../actions/workflows/build.yml) 點進最新一次成功的執行，在頁面下方的 Artifacts 下載 `ExtHost-0.1.0-build.編號`（需要登入 GitHub，保留 30 天）

「關於 ExtHost」會顯示版本號，測試版會帶編譯編號（例如 `0.1.0-build.12`），方便對照是哪一次編譯。

## 自動編譯與發布（GitHub Actions）

設定在 `.github/workflows/build.yml`，在 GitHub 的 Windows 機器上編譯：

| 時機 | 做什麼 |
|---|---|
| 開 PR / 推送到 PR | 執行測試並編譯，確認沒有壞掉（不產出檔案） |
| 合併到 `main` | 執行測試並編譯，產出測試版 Artifact |
| 推送 `v*` 標籤 | 執行測試並編譯，建立 GitHub Release，附上 zip |

**發布正式版**：先把 `src/ExtHost/ExtHost.csproj` 的 `<Version>` 改成新版本並合併，再推送同版本的標籤：

```powershell
git tag v0.2.0
git push origin v0.2.0
```

也可以在 GitHub 的 Releases 頁面按「Draft a new release」直接建立標籤。版本號含 `-`（例如 `v1.0.0-beta.1`）會標成預先發行版。

## 自行編譯

需要 .NET 8 SDK。在 Windows 上執行：

```powershell
.\build.ps1
```

或手動執行：

```powershell
dotnet publish src\ExtHost\ExtHost.csproj -c Release -o dist
```

產出的檔案是 `dist\ExtHost.exe`。要指定版本號可以加上 `-Version`，例如 `.\build.ps1 -Version 0.2.0`（`build.sh` 則是 `./build.sh 0.2.0`）。

執行測試：

```powershell
dotnet test tests\ExtHost.Tests\ExtHost.Tests.csproj
```

測試只涵蓋書籤的資料處理（讀寫、匯入匯出、存檔失敗的保護），不含介面。測試會把資料放在暫存資料夾，不會動到你的書籤。

專案已設定為 self-contained 單一執行檔，也可以在 Linux / macOS 上用 `build.sh` 交叉編譯；不過在非 Windows 環境編譯時，exe 檔案本身不會嵌入圖示（視窗圖示不受影響）。

## 專案結構

```
src/ExtHost/
  App.xaml(.cs)              啟動、檢查 WebView2 Runtime、全域例外處理
  MainWindow.xaml(.cs)       主視窗：分頁列、工具列、快捷鍵、全螢幕、工作階段還原
  ExtensionPopupWindow.cs    擴充功能 popup 視窗（自動調整大小、釘選、DevTools）
  Theme.xaml                 顏色與控制項樣式
  Bookmarks/
    BookmarkBar.cs           書籤列（溢位「»」選單、資料夾下拉選單、滑鼠移過切換）
    BookmarkBubble.cs        星號 / Ctrl+D 的「已加入書籤」小視窗
    BookmarkEditorWindow.cs  編輯書籤、新增網頁、新增 / 重新命名資料夾
    ImportBookmarksWindow.cs 匯入書籤和設定（偵測 Chrome / Edge 設定檔、手動路徑導引、HTML 檔）
    BookmarkManagerPage.cs   書籤管理員（資料夾樹、清單、搜尋、多選、拖放、復原）
    BookmarkDrag.cs          拖放的資料格式與位置提示
    BookmarkUi.cs            書籤圖示、下拉選單、右鍵選單、匯出
  Tabs/
    WebTab.cs                網頁分頁（一個分頁 = 一個 WebView2）
    ExtensionsTab.cs         擴充功能管理分頁
    BookmarksTab.cs          書籤管理員分頁
    ExtensionsPage.xaml(.cs) 擴充功能管理畫面
  Services/
    BrowserEnvironment.cs    共用 WebView2 環境（開啟擴充功能支援）
    ExtensionManager.cs      載入 / 啟用 / 停用 / 重新載入 / 移除 / 監看資料夾
    ManifestInfo.cs          解析 manifest.json、相容性標示
    MatchPattern.cs          content_scripts 網址比對（用於網址列的「注入」標籤）
    UrlHelper.cs             網址列輸入轉換
    BookmarkStore.cs         書籤資料（Bookmarks.json，Chrome 相同格式）
    BookmarkImporter.cs      讀取 Chrome / Edge 書籤檔與 HTML 書籤檔、偵測設定檔
    BookmarkExporter.cs      匯出成 HTML 書籤檔
    FaviconCache.cs          網站圖示快取
    AppSettings.cs / AppPaths.cs
tests/ExtHost.Tests/          書籤資料層的自動測試（xUnit）
.github/workflows/build.yml  自動編譯、測試、產出 Artifact 與 Release
samples/page-helper-demo/    範例擴充功能
```

## 授權

Apache License 2.0，詳見 [LICENSE](LICENSE)。
