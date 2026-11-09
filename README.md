# TokenCLI

公開原始碼：[2023k-work/TokenCLI](https://github.com/2023k-work/TokenCLI)。Windows 使用者可從 [v0.1.2 Release](https://github.com/2023k-work/TokenCLI/releases/tag/v0.1.2) 下載 ZIP，解壓後雙擊 BAT；下方提供完整操作方式。

獨立 C# CLI，透過 Windows Credential Manager 管理 OpenAI／Anthropic API Key；支援固定 Responses API 請求與一般文字生成。**沒有 MCP、Codex 登錄、Python 或 npm runtime 需求。**

## 快速開始：雙擊 BAT 設定 Token

1. 解壓 `TokenCLI-0.1.2-win-x64.zip`，保留檔案的相對位置。
2. 雙擊 **`Start-TokenCLI.bat`** 開啟中文選單。使用完整專案時，直接雙擊 `D:\Desktop\TokenCLI\Start-TokenCLI.bat`；也可使用 `dist\0.1.2\win-x64` 內的 BAT。
3. 按 `1` 設定 OpenAI／ChatGPT API Key，或按 `2` 設定 Claude／Anthropic API Key；只需設定實際使用的供應商。
4. 在提示處貼上或輸入 Token，再按 **Enter**。輸入不會顯示文字或星號，這是正常的隱藏輸入。按 **Esc** 可取消，不儲存本次輸入。
5. 完成後按任意鍵回到選單，再按 `3` 查看是否已儲存，最後按 `0` 離開。

**同一 Windows 帳號設定一次即可，後續執行及重新開機會自動取用。** 不需要每次先開 BAT，也不需要把 Token 傳給呼叫端。BAT 提供憑證管理、說明與手動選取的免費網路診斷，不會呼叫供應商 API 或啟動實驗。

| 選項 | 功能 |
| --- | --- |
| `1` | 設定或更新 OpenAI／ChatGPT Token |
| `2` | 設定或更新 Claude／Anthropic Token |
| `3` | 查看憑證是否存在；`true` 表示已儲存，`false` 表示尚未設定 |
| `4` | 刪除 OpenAI Token，輸入 `Y` 才刪除，`N` 返回 |
| `5` | 刪除 Claude Token，輸入 `Y` 才刪除，`N` 返回 |
| `6` | 查看 CLI 命令說明 |
| `7` | OpenAI DNS／TCP／TLS 網路診斷，不讀金鑰、不發送 HTTP |
| `8` | Claude DNS／TCP／TLS 網路診斷，不讀金鑰、不發送 HTTP |
| `0` | 離開選單 |

`true` 只表示 Credential Manager 中存在憑證，不代表金鑰有效、有 API 權限或有可用額度。選單不會進行真實 API 驗證。

## 使用者先準備什麼

使用發布包中的 **self-contained Windows x64 `TokenCLI.exe`**，需要 Windows x64 與目前使用者可寫入的暫存目錄；已在 Windows 11 x64 驗證。不必另裝 Python、Node.js、npm、.NET Runtime、Git 或開發工具。Windows Credential Manager 是系統內建。第一次正式 API 使用前，需要對應供應商的 API Key 與帳號權限；離線 help／version／schema 及免費測試不需要金鑰。

開發／重建才需要 .NET SDK 8 或更新版本及 .NET 8 targeting pack；本次使用 SDK 9.0.318、.NET 8.0.31 驗證。專案沒有第三方 NuGet 套件。`NuGet.Config` 預設停用套件來源，普通建置與測試可使用已安裝的 targeting pack 離線進行。self-contained 發布首次需從 Microsoft NuGet 來源取得對應 Windows runtime pack；使用者取得已發布 EXE 後不需要下載依賴。

## 設定一次 Token

建議使用上方 BAT 選單。若偏好命令列，可在一般 PowerShell 終端切換至 EXE 所在目錄：

```powershell
cd 'D:\Desktop\TokenCLI\dist\0.1.2\win-x64'
.\TokenCLI.exe credentials set openai
.\TokenCLI.exe credentials set anthropic
.\TokenCLI.exe credentials status
```

BAT 直接呼叫這些命令，由 CLI 隱藏讀取 Token；BAT 不會讀取或暫存 Token。其他程式後續直接使用 `TokenCLI.exe`，不透過互動選單。

`set` 隱藏輸入、不顯示星號；只需設定實際使用的供應商。API Key 存在目前 Windows 使用者的 Credential Manager，目標名稱為 `TokenCLI/openai`、`TokenCLI/anthropic`。**後續執行與重新開機都不需再輸入**；換使用者、憑證被刪除／撤銷／過期或需輪替時才重新設定。再次 set 可覆寫該供應商的舊 Token。

不接受金鑰命令列參數、不接受由 stdin 管線設定、不提供讀出金鑰指令；不使用環境變數、登錄檔或明文檔案 fallback。這是新的 TokenCLI 憑證命名空間，不會自動讀取或搬移 TokenBridge 的既有金鑰；遷移時請使用者在此工具重新設定一次。金鑰使用時仍會短暫存在程序記憶體，同一 Windows 帳號的其他程式仍可能存取該帳號憑證，並非惡意同權限程序的隔離沙箱。

移除：

```powershell
.\TokenCLI.exe credentials delete openai
```

## BAT 常見問題

- **找不到 TokenCLI.exe**：完整解壓 ZIP，不要只複製 BAT。BAT 可放在 EXE 旁邊，或完整專案根目錄並保留 `dist\0.1.2\win-x64\TokenCLI.exe`。
- **輸入 Token 沒有任何字元**：這是隱藏輸入，貼上或輸入後按 Enter，不需要因為沒有星號而重複貼上。
- **另一個終端／程式顯示未設定**：確認使用相同 Windows 帳號。不同帳號或服務帳號的 Credential Manager 不共用。
- **金鑰已儲存但 API 失敗**：狀態查詢不會測試金鑰是否有效；應另確認供應商 API Key、權限及額度。需更換時再次選 `1` 或 `2` 即可。
- **需要檢查 BAT 是否找到 EXE**：在 PowerShell 執行 `.\Start-TokenCLI.bat --check`，只輸出版本，不查詢憑證或呼叫 API。

## 免費命令

```powershell
.\TokenCLI.exe help
.\TokenCLI.exe version
.\TokenCLI.exe schema input
.\TokenCLI.exe schema output
.\TokenCLI.exe schema generate-text
.\TokenCLI.exe schema network
.\TokenCLI.exe doctor network
.\TokenCLI.exe doctor network anthropic
```

doctor 會明確進行下述網路檢查；其他 help／version／schema 是離線操作。沒有自動 API 連線測試。`credentials status` 只回報有無憑證，不回傳內容，也不測試金鑰是否有效。下列 responses／generate-text 命令則**會連供應商並可能計費**；本次開發沒有呼叫它們的真實 API。

## 免費網路診斷：doctor network

```powershell
.\TokenCLI.exe doctor network
.\TokenCLI.exe doctor network openai
.\TokenCLI.exe doctor network anthropic
```

預設檢查 `api.openai.com:443`；anthropic 對應 `api.anthropic.com:443`。只接受上述 provider，不接受自訂網址、IP、port、代理或任意命令。也可在 BAT 選 `7`／`8`，不必輸入 Token。

依序檢查 DNS → TCP → TLS；每階段最多 10 秒，失敗後停止，後續階段標為 not_run。DNS 解析後只選一個候選位址（IPv4 優先），只做一次 TCP connect，不測試所有 IP、不進行應用層重試。不使用代理，與 API 傳輸的 direct 模式一致。

**不讀 Credential Manager、不傳金鑰、不建立任何 HTTP GET／HEAD／POST，也不呼叫 count／generate。** TLS 採 OS 預設協定與正常憑證／主機名稱驗證，觀察 callback 只接受 SslPolicyErrors.None；不忽略任何驗證錯誤，不會安裝憑證或調整安全政策。

輸出為獨立的診斷 JSON，Schema 可用 `schema network` 取得，檔案見 [network-doctor.output.json](schemas/network-doctor.output.json)。含各階段 status／elapsed_s、安全分類、socket_error、native_error_code、hresult；TLS 另保留憑證錯誤旗標、憑證鏈狀態數碼、原生安全錯誤碼與 HRESULT 數碼鏈。沒有原始 exception 訊息、stack trace、cert subject／issuer、IP 清單或 HTTP headers。未知欄位保持未知，不補成成功。

| 結果 | 意義及下一步 |
| --- | --- |
| dns_name_resolution_failed | 此環境無法解析固定主機，檢查允許的 DNS／網路政策 |
| tcp_access_denied | socket 操作被拒絕，檢查實際執行程序的權限／端點政策 |
| tcp_connection_refused／tcp_unreachable | 候選主機 TCP 443 無法連通，確認正常網路及防火牆規則 |
| tls_security_context_unavailable | Windows 安全套件回報 0x8009030E，屬於程序的 TLS 安全 context 問題；不是 OpenAI／Anthropic API Key 失效的證據 |
| tls_certificate_validation_failed | 證書缺失、名稱或信任鏈驗證未通過；與組織管理者確認信任設定，不要停用驗證 |
| tls_handshake_failed | 其他 TLS 協商失敗；可提供安全數碼進一步查核，不能自行歸因為金鑰或額度 |
| dns_timeout／tcp_timeout／tls_timeout | 該階段超過期限；後續不執行 |
| ok=true | 本次單一候選位址的 DNS／TCP／TLS 通過，不代表 HTTP、API 權限、額度或生成可用 |

若代理執行環境失敗，請使用者在實際的一般 PowerShell 執行同一命令比較；無需預設以管理員執行。診斷不會清除呼叫端的 halt、修改帳本、重送先前失敗的計數或自動安排下一筆操作；恢復實驗仍由呼叫端自己的協定與授權控制。

本版採版本目錄與舊版並存。保留原 `dist/win-x64/TokenCLI.exe` 及舊 ZIP；請使用新路徑測診斷，不要以新版 EXE 覆蓋其他流程已固定雜湊的舊工具。來源／工具版本變更應由呼叫端另外核對。

[Microsoft Windows 安全錯誤碼說明](https://learn.microsoft.com/en-us/windows/win32/com/com-error-codes-4)

## 程式整合：一個程序、一份輸入、一個結果

命令：`TokenCLI.exe responses`。stdin 必須是 UTF-8（無 BOM）完整 JSON `{operation,payload}`，輸入完畢後關閉 stdin；不是持續等待多筆的 JSONL 服務。stdout 只輸出一份 JSON 證據，無啟動畫面或診斷混入。不要將 API Key 放在 JSON 中。

完整 count 範例見 [examples/responses-count.json](examples/responses-count.json)。保留訊息陣列，所有固定欄位必填：

```json
{
  "operation": "count",
  "payload": {
    "model": "gpt-6.1-sol",
    "reasoning": {"effort": "medium"},
    "service_tier": "default",
    "max_output_tokens": 3072,
    "tools": [],
    "tool_choice": "none",
    "store": false,
    "stream": false,
    "truncation": "disabled",
    "prompt_cache_options": {"mode": "explicit", "ttl": "30m"},
    "input": [{"role": "user", "content": [{"type": "input_text", "text": "完整公開輸入"}]}]
  }
}
```

operation 僅 count／generate；端點分別固定為 `https://api.openai.com/v1/responses/input_tokens` 與 `/v1/responses`。count 投影相同 payload 的 model、input、reasoning、tools、tool_choice、truncation；其他固定欄位不是計數端點參數，仍須通過完整契約驗證。generate 原樣傳遞完整訊息結構，沒有自動補值、串接 prompt 或截斷。

僅接受 system／developer／user／assistant 的純文字訊息；content 可為字串或 input_text 區塊。禁止未知欄位、URL 參數、任意方法、遠端檔案、工具、快取斷點、previous_response_id、conversation、Shell／Python 指令欄位。文字中的 URL 只是文字，不會被下載。完整 stdin 上限 262144 bytes，最多 256 則訊息、每則最多 256 個區塊。重複 JSON key、無效 JSON／非有限數拒絕。

機器可讀契約見 [輸入 Schema](schemas/responses.input.json) 與 [輸出 Schema](schemas/responses.output.json)，兩份描述固定 Responses 請求及通用稽核結果；程式在送出前以等價的嚴格規則驗證，schema 指令可離線輸出內嵌版本。

取得 count 不會自行生成。呼叫端負責每次呼叫前持久保留次數與費用、完整計數後確認不超過 4096 tokens，才使用同一 payload 明確發起 generate。未知結果保留全額並停止。CLI 不重建帳本、不結算費用、不評分、不排程，不執行模型回傳的程式碼。選用的 Python 呼叫範例見 [examples/cli_client.py](examples/cli_client.py)，預設只查 CLI version；Python 是範例呼叫端的選擇，TokenCLI 不需要它。

## 稽核結果與失敗語意

stdout 證據保留 HTTP 狀態、安全的供應商 request ID、response ID、**實際回傳** model／service_tier／reasoning、status／incomplete_details、完整巢狀 usage、所有輸出文字與耗時。沒有 usage 時維持 null，不補零；不從請求補出供應商 model。`bridge_request_id` 沿用原 Schema 名稱，是本機關聯 UUID，與供應商 ID 分開。

`raw_response_json` 是解析、認證遮蔽後重新序列化的完整 JSON，並非原始 HTTP bytes；順序／空白／escape 表達可能改變。巢狀欄位保留，認證反射與 auth 欄位值遮蔽。`present_fields` 配合 raw JSON 可區分缺失與明確 null。HTTP 錯誤不回傳原始文字、headers 或錯誤 body。`ok=true` 只代表收到可解析 2xx JSON，不代表模型完成、符合呼叫端契約或費用可結算。

| outcome | 意義 |
| --- | --- |
| not_sent | 驗證／憑證／傳輸準備未通過，未進入 HTTP send |
| attempted_unknown | 已嘗試送出，但完成證據未知；可能已有 HTTP 200 但讀取、大小或解析失敗 |
| http_error | 收到非 2xx，含 429、重新導向；不推斷費用 |
| response_received | 收到可解析 2xx JSON，仍由呼叫端審核完成狀態、用量與費用 |

沒有 calculated_usd／billed_usd，也不宣稱 timeout、429、取消或中斷為零費用。程序被殺、stdout 遺失或呼叫端逾時可能完全沒有結果；呼叫端必須依既有帳本保留費用、停止且不重送。CLI 本身沒有跨程序去重；呼叫端重啟並再次呼叫就是新請求。

| exit code | 意義 |
| --- | --- |
| 0 | 命令成功；對 API 僅表示有效傳輸結果，不代表模型已完成 |
| 2 | 無效參數／輸入、憑證問題、HTTP 錯誤或本機錯誤；請解析 JSON |
| 3 | API 嘗試後結果未知，或 doctor 網路檢查失敗；請先依輸出 Schema 區分命令，doctor 不代表有 API 嘗試 |
| 130 | 使用者取消；若已嘗試則仍保留費用並停止 |

## 一般 OpenAI／Anthropic 文字呼叫

命令 `TokenCLI.exe generate-text` 接收 `{provider,model,prompt,max_output_tokens?}`；參考 [OpenAI](examples/openai.json)、[Anthropic](examples/anthropic.json) 範例。model 由使用者明確指定為自己帳號可用的 API model ID；prompt 1–32000 個 .NET 字元，輸出上限 16–8192（預設 1024）。回傳同樣的證據格式，完整 Anthropic stop_reason 等欄位保留在 raw_response_json。呼叫端必須使用 responses 命令，不以一般命令繞過固定實驗設定。

## 傳輸與安全

每個程序只送出一次 HTTP，不使用 AI SDK 或重試迴圈。強制新 HTTP/1.1 連線、Connection: close，避免 HTTP/2 重送與舊連線重用；不跟隨重新導向、不使用系統／環境代理、不傳 cookie。目的地固定，TLS 採用 OS 的正常憑證驗證，沒有忽略驗證選項。

送出及讀取共用 90 秒期限；回應上限 4000000 bytes，同時檢查 Content-Length 與逐塊實際大小，拒絕壓縮回應。取消會取消傳輸，不會重新送出。應用程式不建立含金鑰／請求／回應的日誌，錯誤只輸出固定分類。金鑰僅在 TLS 中的供應商認證 header 使用，不進 request body。沒有修改 Codex 設定或啟動外部腳本的功能。

## 建置、測試及發布

```powershell
.\Build-Test.cmd
.\Publish-Windows.cmd
```

免費測試以自製 HttpMessageHandler／假憑證驗證端點、契約、單次呼叫、usage、錯誤與取消、大小限制、遮蔽；另驗證真正的 CLI 子程序管線，以及隨機專用命名空間內的 Windows Credential Manager 假金鑰寫入／更新／刪除。正式 TokenCLI／TokenBridge 憑證不變動。

新版發布產物為 `dist/0.1.2/win-x64/TokenCLI.exe`，self-contained 單檔含 .NET runtime。編譯產生的普通 `bin/Release/net8.0/TokenCLI.exe` 則需要 .NET 8 Runtime，請勿與正式 self-contained 發布包混淆。

## 官方參考

- [Windows CredWrite](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)
- [.NET 單檔與 self-contained 發布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Responses 建立](https://developers.openai.com/api/reference/python/resources/responses/methods/create)
- [輸入計數](https://developers.openai.com/api/reference/resources/responses/subresources/input_tokens/methods/count)
- [Anthropic Messages](https://platform.claude.com/docs/en/api/messages)
