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
- **依賴瀏覽器介面的 API**（`contextMenus`、`sidePanel`、`commands`、`tabs` 的部分功能等）：可能無法使用。管理頁會把這類權限標成橘色「需實測」，但這只是依權限名稱標示，實際能不能用還是要測
- `chrome.runtime.openOptionsPage()` 可能無效，建議改用 `window.open(chrome.runtime.getURL('options.html'))`，ExtHost 會開成新分頁
- **重新載入方式**：預設是「移除後重新加入」，一定會讀到新檔案，但**該擴充功能的 `chrome.storage` 資料可能被清除**。如果要保留資料，把 `settings.json` 的 `ReloadMode` 改成 `"toggle"`（停用再啟用），但部分修改可能不會生效

建議把擴充功能設計成以 content script + storage + runtime 訊息 + fetch 為核心、UI 直接注入頁面。這樣的擴充功能在 ExtHost 和一般 Chrome 都能跑。

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

要修改設定，可以從右上角選單 →「編輯 settings.json」開啟。**請先關閉 ExtHost 再改**，因為程式關閉時會覆寫這個檔案。

## 自行編譯

需要 .NET 8 SDK。在 Windows 上執行：

```powershell
.\build.ps1
```

或手動執行：

```powershell
dotnet publish src\ExtHost\ExtHost.csproj -c Release -o dist
```

產出的檔案是 `dist\ExtHost.exe`。專案已設定為 self-contained 單一執行檔，也可以在 Linux / macOS 上用 `build.sh` 交叉編譯；不過在非 Windows 環境編譯時，exe 檔案本身不會嵌入圖示（視窗圖示不受影響）。

## 專案結構

```
src/ExtHost/
  App.xaml(.cs)              啟動、檢查 WebView2 Runtime、全域例外處理
  MainWindow.xaml(.cs)       主視窗：分頁列、工具列、快捷鍵、全螢幕、工作階段還原
  ExtensionPopupWindow.cs    擴充功能 popup 視窗（自動調整大小、釘選、DevTools）
  Theme.xaml                 顏色與控制項樣式
  Tabs/
    WebTab.cs                網頁分頁（一個分頁 = 一個 WebView2）
    ExtensionsTab.cs         擴充功能管理分頁
    ExtensionsPage.xaml(.cs) 擴充功能管理畫面
  Services/
    BrowserEnvironment.cs    共用 WebView2 環境（開啟擴充功能支援）
    ExtensionManager.cs      載入 / 啟用 / 停用 / 重新載入 / 移除 / 監看資料夾
    ManifestInfo.cs          解析 manifest.json、相容性標示
    MatchPattern.cs          content_scripts 網址比對（用於網址列的「注入」標籤）
    UrlHelper.cs             網址列輸入轉換
    AppSettings.cs / AppPaths.cs
samples/page-helper-demo/    範例擴充功能
```

## 授權

Apache License 2.0，詳見 [LICENSE](LICENSE)。
