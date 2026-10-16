# Louisa Wi-Fi（Windows 版）

走進任何一家路易莎，Windows 自動連上 Wi-Fi。

Mac 版的 Windows 移植，邏輯相同：先認連過的路由器，再用位置找最近的門市，依序試門市電話當密碼。支援 Windows 10 / 11，不用另外安裝任何東西。

## 安裝

1. 下載 **[LouisaWiFi-Windows.exe](https://github.com/madeyexz/louisa-wifi/releases/latest/download/LouisaWiFi-Windows.exe)**，點兩下。
2. 如果跳出藍色的「Windows 已保護您的電腦」，按 **「其他資訊」→「仍要執行」**。
   （這個程式沒有付費的數位簽章，第一次打開時 Windows 會這樣提醒，是正常的。）
3. 問「要安裝嗎？」時按 **「是」**。

裝好後右下角（可能收在 `^` 裡面）會出現路易莎圖示，之後每次開機都會自動在背景執行。開始功能表也找得到「Louisa Wi-Fi」。

### 開啟定位（建議）

第一次開啟時會問你要不要開定位。自動找出你在哪一家門市需要它：

**設定 → 隱私權與安全性 → 定位**，打開「定位服務」和「讓桌面應用程式存取您的位置」。

> Windows 11 24H2 之後，連「掃描附近 Wi-Fi」都需要開定位。
> 不想開也可以，第一次到某家店時從選單選門市，之後到同一家店就會自動連上。

## 怎麼用

**什麼都不用做。** 到了路易莎，幾秒內就會自動連上，並跳出通知，例如「已連上：板橋環球門市」。

右下角圖示：**彩色**＝已連上路易莎、**灰色加藍點**＝連線中、**灰色**＝沒連上。點圖示打開選單：

- **用密碼連線**：店裡有貼密碼時用。列出最近那家店的候選密碼，或「輸入密碼…」照店內張貼的輸入，連上後會記住
- **附近門市**：最近 6 家，點一下就連
- **所有門市**：依縣市分類，每家店可以「自動嘗試」、指定某組密碼，或「輸入密碼…」
- **搜尋門市…**：打店名、地址或電話（例如「卓越」「羅斯福」「2367」）
- **連線統計…**：在瀏覽器看每天、每家門市、每月的連線時間
- 「自動連線」「只顯示連過的門市」「開機時自動執行」的開關
- **不在系統匣顯示…**：程式繼續在背景跑；想再顯示時，從開始功能表再打開一次 Louisa Wi-Fi

已經連著其他可以上網的 Wi-Fi（例如手機熱點）時不會切換。連著別的 Wi-Fi 卻連續 30 秒不能上網，而附近有路易莎 Wi-Fi，就會自動換過去。

## 移除

**設定 → 應用程式 → 已安裝的應用程式**，找到「Louisa Wi-Fi」→ 解除安裝。會問你要不要一併刪除連線紀錄和統計。

## 資料存在哪

全部只在你的電腦上，`%APPDATA%\LouisaWiFi\`：

| 檔案 | 內容 |
| --- | --- |
| `learned.json` | 連過的路由器 → 門市與密碼 |
| `usage.json` | 每天在各門市的連線時間 |
| `stores.json` | 門市資料（每週從原專案自動更新） |
| `config.json` | 設定 |
| `LouisaWiFi.log` | 除錯紀錄（不記錄位置） |

程式本身裝在 `%LOCALAPPDATA%\Programs\LouisaWiFi\`。網路連線只有：`msftconnecttest.com`（Windows 用來確認能不能上網的同一個網址）和 GitHub（更新門市資料）。

## 已知限制

- Windows 記住的 LouisaCoffee 會設成「手動連線」，由本程式決定何時連；所以程式沒在執行時，Windows 不會自己連路易莎。
- 筆電沒有 GPS，定位靠附近的 Wi-Fi，偶爾不準，第一次去某家店可能要手動選。
- 少數門市的密碼不照電話號碼規則，自動連線會失敗，請用「輸入密碼…」。

## 從原始碼編譯

Windows 內建的 C# 編譯器就夠了，在 repo 根目錄執行：

```
powershell -ExecutionPolicy Bypass -File windows\build.ps1
powershell -ExecutionPolicy Bypass -File windows\test.ps1
```

產生 `windows\dist\LouisaWiFi.exe`，門市資料和圖示用 repo 裡的 `data/stores.json` 和 `app/AppIcon.png`。開發細節見 [AGENTS.md](AGENTS.md)。程式碼在 `src\`：

| 檔案 | 內容 |
| --- | --- |
| `Program.cs` | 進入點、安裝、移除 |
| `TrayApp.cs` | 系統匣圖示、選單 |
| `Joiner.cs` | 找門市、試密碼、連線（對應 Mac 版 `Joiner.swift`） |
| `Wlan.cs` | Windows 原生 Wi-Fi API |
| `Stores.cs` | 門市資料、定位 |
| `Tracker.cs` | 連線時間統計 |
| `Dialogs.cs` | 輸入密碼、搜尋門市 |

## 授權

MIT，同本專案。
