# Framing Buddy（Unity 版）

攝影師的拍攝規劃工具：選擇世界上任何地點、站立位置與高度、鏡頭方向與焦段、日期時間與天氣，
即時看到相機會拍到的畫面。操作與網頁版（`../framing-buddy`）相同，分享連結互通；
渲染改用 Unity 6 HDRP，追求照片級的光線與大氣。

## 需求

- macOS（Apple Silicon，M5 MacBook Air 以上）
- Unity 6000.3.25f1（HDRP 17.3、Cesium for Unity 1.25.1、glTFast 6.20）
- Google Maps Platform「桌面用」金鑰（選用，見下）

## 建置

```sh
U=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/MacOS/Unity
# 第一次：建立 HDRP 資產、材質、地標預製物、場景（之後改到材質或設定時再跑）
"$U" -batchmode -nographics -projectPath . -executeMethod FramingBuddy.EditorTools.ProjectSetup.Run -quit
# 正式版（IL2CPP）→ Builds/Framing Buddy.app
"$U" -batchmode -nographics -projectPath . -executeMethod FramingBuddy.EditorTools.ProjectSetup.Build
# 開發版（Mono，建置較快）→ Builds/dev/
"$U" -batchmode -nographics -projectPath . -executeMethod FramingBuddy.EditorTools.ProjectSetup.BuildDev
```

在編輯器中也可從選單「Framing Buddy → Setup Project／Build macOS App」執行。

## Google 金鑰

網頁版的金鑰有 HTTP referrer 限制，桌面程式無法使用。請另建一把金鑰：

1. Google Cloud Console → API 和服務 → 憑證 → 建立 API 金鑰。
2. 應用程式限制選「無」（或 macOS 應用程式），API 限制勾選：
   **Map Tiles API**（3D 實景圖磚與 2D 地圖）、**Places API (New)**（搜尋）、**Time Zone API**。
3. 在 App 的「場景載入」區塊貼上並按「儲存並重新載入」。金鑰只存在這台電腦的使用者設定（PlayerPrefs）。

開發時也可以放在 `Assets/StreamingAssets/config.local.json`：`{"googleKey": "…"}`（已列入 .gitignore）。

沒有金鑰時：地圖用 OpenStreetMap、搜尋用 Nominatim、地形用 AWS Terrain Tiles、
建物用 OSM（近景 Overpass 全部建物、遠景 OpenFreeMap 向量圖磚的高樓），自建地標模型照常顯示。

## 功能對照

| | 內容 |
|---|---|
| 位置／高度／方向 | 快速位置（中正紀念堂、哈爾格林姆教堂、富士山周邊、世界拍攝點）、座標前往、地點搜尋、自動貼合地面／屋頂 |
| 鏡頭 | 全片幅等效焦段 8–1200 mm、畫面比例、橫直幅、光圈與對焦距離（實體相機景深）、超焦距 |
| 光線 | 當地日期時間（時區自動判斷）、日出日落／金色／藍色時刻、太陽月亮位置、月相、雲量、能見度、曝光補償 |
| 測光 | Sony 多重測光近似（偏重中央的權重遮罩＋直方圖排除高光）、中央重點、點測光、平均；HUD 顯示建議快門 |
| 場景 | Google 3D 圖磚（遠景）＋自建精細模型（近景，挖空 Google 模型）、遠方顯著山峰的 DEM 精細山體（富士山等）、湖泊水面與倒影 |
| 天空 | Physically Based Sky（大氣散射、地影）、真實星表＋銀河（依恆星時旋轉）、體積雲、高度霧＋體積光、HDRP 月相 |
| 地圖 | Google 2D 圖磚或 OSM；視角扇形、近景／遠景範圍、太陽／月亮／目標方向線；3D 環視（視錐） |
| 取景器 | 三分法、中心、水平線、目標標記與畫面外箭頭、可見度（遮擋）判斷 |
| 分享與輸出 | 分享連結（與網頁版相同格式，Cmd+C／Cmd+V）、儲存畫面 PNG、8K 高解析輸出（存到「圖片／Framing Buddy」） |

快捷鍵：W/S 前進後退、A/D 平移、R/F 升降、方向鍵轉向與俯仰、+/- 變焦、Shift 加速；
取景器上拖曳轉動鏡頭、滾輪變焦；Cmd+Ctrl+F 切換全螢幕。

## 效能（M5 MacBook Air）

- 預設「精細（高）」畫質：TAA＋STP 動態解析度（依 GPU 時間在 50–100% 之間調整）、
  取景框貼圖為螢幕像素的 0.85 倍、體積雲效能模式、體積霧中等品質；
  「極致」再開啟螢幕空間全域光照、高品質雲與霧（建議 M5 Pro／Max）。
- 60 fps 上限、不做垂直同步（macOS 視窗經合成器，不會撕裂）；
  畫面靜止 1.5 秒後降到 20 fps，操作時立即回到 60 fps，讓無風扇機身維持低溫。
- 實體相機景深用中等品質：高品質（大量取樣）在 M 系列 GPU 上要 80 ms 以上。

## 測試用命令列參數

```
"Builds/Framing Buddy.app/Contents/MacOS/Framing Buddy" \
  -preset "大石公園" -time 07:40 -date 2026-11-15 -quality High \
  -url "#c=0.1&mm=Multi" -shot /tmp/shot.png -shotdelay 8 -quit
```

`-url`（分享連結或 hash）、`-preset`（快速位置名稱片段）、`-time`、`-date`、`-quality`、
`-shot`（場景載入完成後截圖）、`-shotdelay`、`-quit`、`-profile <raw>`（開發版錄製 Profiler，
以 `ProfileReport.Run -profileFile` 摘要）、`-fixedev <EV>`、`-disablecomp <Volume 元件>`、
`-nowater`、`-noprobe`、`-notrees`。

## 程式結構

- `Scripts/Core`：座標（與網頁版相同：原點＋x 東 z 南）、鏡頭、狀態與分享連結、時區、日月計算、網路
- `Scripts/World`：Cesium 地理參考與 Google 圖磚、自建地標、DEM 地形與精細山體、OSM 建物、水域、目標
- `Scripts/Rendering`：天空與光線（Celestial）、星空、取景相機（PhotoRig）、地圖 3D、遮擋判斷
- `Scripts/UI`：UI Toolkit 介面（全部以程式建立）、地圖、搜尋、取景器疊加層
- `Editor`：專案設定與建置、Profiler 摘要
- `Landmarks/*.glb`：由網頁版匯出的中正紀念堂、台北 101、哈爾格林姆教堂（含鞦韆）模型
