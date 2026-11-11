# 免費驗證紀錄

0.2.0 新增 generate-image 與 BAT 選項 9，固定 OpenAI Images endpoint、每次一張、Schema 嚴格驗證、獨立 timeout／大小上限、單次傳輸；沿用 Credential Manager。API 引擎抽出共用驗證與傳輸路徑，原文字與計數行為由既有 80 項測試保護。

新增 20 項測試（內含參數與錯誤組合）驗證精確請求、三種檔案格式簽章、完整 usage／實際模型、不補造缺失欄位、輸入注入／重複 key、秘密反射（含解碼圖片）、壓縮／大小限制、HTTP 401／429／500／302／307、timeout／取消／連線錯誤不重試、本機 preflight、原檔保護、暫存檔清理及 API 成功後檔名碰撞。回歸亦驗證真正 CLI 子程序的新 Schema、無效產圖輸入與拒絕管線互動模式。另以 Python jsonschema 對成功／儲存失敗證據與範例做免費核對。

免費測試未呼叫真實產圖 API。另於 2026-11-11 經使用者授權完成一次真實產圖 smoke test：請求模型 gpt-image-2、1024×1024、中品質 PNG，HTTP 200，約 31.30 秒，input_tokens=141、output_tokens=1756；圖片 SHA-256 核對通過，沒有重試。供應商沒有回傳 model，因此證據中仍為 null。這次成功不代表其他模型／帳號、透明度或帳單金額已驗證。測試紀錄及生成圖片僅保留本機，不納入公開提交。圖片不經完整影像 decoder，僅檢查格式簽章；沒有實測斷電、磁碟耗盡或所有 OS 權限情境。先前另行授權的文字付費測試不計入本次免費測試。

0.2.0 原始碼推送 main；本機交付 EXE、ZIP 與 SHA256SUMS，尚未建立 0.2.0 GitHub Release。舊版 0.1.1 與 0.1.2 的版本目錄、EXE／ZIP 不覆寫。

0.1.2 更新：新增固定主機 doctor network、各階段 timeout／取消與安全錯誤分類，TLS 憑證觀察不接受任何驗證錯誤。新增輸出 Schema 與 BAT 選單；原 API 契約與傳輸不變。80 項免費測試通過，含新診斷與既有回歸。

實際受限制執行環境探測：DNS 與 TCP 通過、TLS 回傳 0x8009030E（tls_security_context_unavailable）；沒有金鑰存取或 HTTP 請求。這是目前程序環境的證據，不追溯宣稱任何舊 API 嘗試的確切根因。原生安全錯誤的 JSON 數字陣列序列化問題曾由回歸測試重現，修正後通過，並以真實 CLI 輸出與 Schema 再核對。

0.1.1 更新：文件與公開 CLI 說明改為通用 Responses 用途，新增 `responses` 命令，保留原有命令相容入口與 JSON 契約。下列測試已對新版重新執行，涵蓋兩個入口的子程序輸入驗證，以及公開說明不含特定專案名稱。

本次在 Windows x64、.NET SDK 9.0.318／.NET 8.0.31 完成建置，0 警告、0 錯誤。

**0.2.0：100 項測試全部通過，本次產圖開發與回歸測試的真實 AI API 呼叫為 0。**

- 精確固定 Responses count／generate 端點、完整訊息結構與固定參數。
- 原始模型／服務層級／推理設定、完整巢狀用量、輸出串接及缺失欄位不補造。
- 未知欄位、URL／method／provider 注入、錯誤模型／推理／工具／store／stream／truncation、對話延續、檔案、快取斷點、錯誤角色與 JSON 重複 key 拒絕。
- 401／403／429／500、302／307 重新導向、逾時、網路錯誤與取消只嘗試一次；沒有推算或填入費用。
- 原始回應上限、Content-Length 預檢、逐塊大小上限、壓縮拒絕、無效 JSON／非有限數與 HTTP 2xx error body 處理。
- 金鑰反射在文字、metadata、巢狀 usage、JSON key／Unicode escape 與 header 皆遮蔽或拒絕。
- 一般 OpenAI／Anthropic 請求格式與文字回應。
- Windows Credential Manager 原生假金鑰寫入、更新、讀取、移除，並實際啟動另一程序讀取已儲存的假金鑰，驗證後續程序免再次輸入。
- 真正 CLI 子程序 stdin／stdout 管線與發布版 self-contained EXE 的 version、schema、無效輸入及拒絕管線設定金鑰；沒有實際供應商 HTTP。

憑證測試只使用 `TokenCLI.Tests/<隨機UUID>`；不讀寫正式 TokenCLI／TokenBridge 的 API Key。沒有測試真實帳號權限、生成／計數、帳單或斷電重啟，不能把 mock 通過視為實驗已獲授權或已完成。

已產生約 35 MB 的 self-contained 單檔 `dist/0.2.0/win-x64/TokenCLI.exe`，內含 .NET runtime；使用者無需安裝 Python、npm 或 .NET runtime。

重現：`Build-Test.cmd`。若要讓程序測試使用發布版：

```powershell
.\tests\TokenCLI.Tests\bin\Release\net8.0\TokenCLI.Tests.exe --cli '完整發布路徑\TokenCLI.exe'
```

免費驗證沒有修改 Codex 設定、外部專案程式／帳本。原始碼與 Windows 發布包由使用者要求發布至公開 GitHub 倉庫。
