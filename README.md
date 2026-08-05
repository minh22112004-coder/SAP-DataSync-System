# SAP DataSync System

MVP đồng bộ dữ liệu theo luồng:

```text
SAP → export.xlsx hoặc export_YYYYMMDD_HHmmss.xlsx → Snapshot archive → Python ETL Worker → SQL Staging → Insert/Update/Soft Delete → Audit Log → Web App
```

Giai đoạn 1 cung cấp nền tảng Docker và database. Giai đoạn 2 sử dụng Python ETL Worker để tự động import Excel và đồng bộ SQL mỗi ngày.

## Thành phần

- `web-api`: Web tối thiểu, health endpoint và database bootstrap.
- `etl-worker`: Python Worker đọc Excel, nạp Staging, đồng bộ insert/update/soft delete và ghi audit.
- SQL Server 2022 được cài trực tiếp trên Windows host và quản trị bằng SSMS.

## Quyết định kiến trúc

```text
Docker Compose
├── web-api        ASP.NET Core
└── etl-worker     Python — chạy ETL mỗi ngày

Windows host
└── SQL Server 2022 (MSSQLSERVER, TCP 1433)
```

Python ETL dùng APScheduler, `openpyxl(read_only=True)`, `pyodbc`, `hashlib` và stored procedure SQL. ASP.NET Web API gọi Groq API trực tiếp cho Giai đoạn 4 nên không cần AI container hoặc mô hình local. Importer C# được giữ trong source làm bản tham chiếu nhưng không còn chạy trong Docker Compose.

## Yêu cầu

- Docker Engine hoặc Docker Desktop có Docker Compose.
- SQL Server 2022 đã cài trực tiếp, bật Mixed Mode và TCP/IP port 1433.
- SQL Server license phù hợp trước khi dùng production.

Người dùng cuối không cần cài Docker; Docker chỉ chạy trên máy phát triển hoặc máy chủ triển khai.

## Windows Launcher — Giai đoạn 5

Máy Windows triển khai hệ thống có thể dùng giao diện `SapDataSync Launcher` thay cho việc nhập lệnh Docker Compose. Launcher chạy bên ngoài Docker và cung cấp:

- **Khởi động hệ thống**: tự mở Docker Desktop nếu cần; lần chạy đầu sẽ build image, các lần sau chỉ khởi động container.
- **Dừng hệ thống**: dùng `docker compose stop` cho Web API/ETL; không dừng SQL Server Windows service.
- **Mở Web App**: mở đúng cổng `WEB_PORT` trong trình duyệt.
- **Làm mới trạng thái**: kiểm tra trực tiếp SQL Server host và trạng thái Web App/ETL mỗi 10 giây.

Chạy Launcher trong môi trường phát triển:

```powershell
dotnet run --project .\src\Launcher\SapDataSync.Launcher.csproj
```

Tạo file `.exe` self-contained cho Windows x64, không yêu cầu cài .NET Runtime:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish-launcher.ps1
```

File kết quả nằm tại `artifacts\launcher\win-x64\SapDataSync.Launcher.exe`. Giữ file trong bộ thư mục triển khai có `compose.yaml`; Launcher tự tìm thư mục gốc từ vị trí của nó. Có thể đặt biến môi trường `SAPDATASYNC_ROOT` khi cần lưu Launcher ở vị trí khác.

Tạo bộ cài đặt Windows có shortcut và uninstaller an toàn:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-installer.ps1 -Version 1.1.1
```

File kết quả nằm tại `artifacts\installer\SapDataSync-Setup-1.1.1.exe`. Bộ cài không chứa `.env`, API key, database hoặc file Excel. Uninstaller không tự xóa Docker volume, SQL Server hoặc dữ liệu người dùng.

Docker vẫn chạy Web API và ETL Worker. Nếu chưa có `.env`, Launcher sao chép cấu hình từ `.env.example` và không tự thay đổi mật khẩu của SQL Server đã cài trên host. AI vẫn mặc định tắt cho tới khi API key được cấu hình an toàn.

## Cấu hình

Tạo file `.env` từ mẫu:

```powershell
Copy-Item .env.example .env
```

Mở `.env`, kiểm tra `SQL_HOST`, `SQL_PORT`, `SQL_USER`, `SQL_PASSWORD` và thay mật khẩu mẫu bằng mật khẩu thực tế của SQL Server. Không commit `.env`.

Mặc định, ETL Worker đọc file khớp `export*.xlsx` trong `data/source`. Đặt bản sao file local vào thư mục này:

```powershell
Copy-Item -LiteralPath .\export.xlsx -Destination .\data\source\export.xlsx
```

`data/source` bị Git ignore và được mount vào container ở chế độ read-only. File upload và snapshot được lưu bền vững trong hai Docker named volume riêng là `uploads_data` và `archive_data`.

### Snapshot file nguồn SAP

- ETL chấp nhận cả tên cố định `export.xlsx` và tên có thời gian như `export_YYYYMMDD_HHmmss.xlsx` thông qua mẫu `export*.xlsx`.
- Trước khi import, ETL tính SHA-256 và sao chép nguyên trạng file mới vào `data/archive`. Chỉ snapshot đã sao chép hoàn chỉnh và có hash khớp mới được đưa vào Staging.
- Snapshot có dạng `<tên hoặc thời gian file>_<SHA256>.xlsx`. Cùng một nội dung sẽ tái sử dụng snapshot hiện có, không tạo bản sao trùng.
- `data/source` luôn chỉ đọc; ETL không ghi đè, đổi tên hoặc di chuyển file do SAP tạo.
- Nếu SAP hỗ trợ, vẫn nên export thành file tạm `.tmp` rồi đổi tên thành `.xlsx` khi hoàn tất. ETL không quét `.tmp` và còn kiểm tra tuổi tối thiểu/hash để tránh đọc file đang ghi dở.
- Nếu SAP ghi đè `export.xlsx` nhiều lần giữa hai lượt quét, ETL chỉ lưu được phiên bản tồn tại tại thời điểm quét. Muốn giữ mọi phiên bản, SAP phải tạo tên riêng cho từng lần export hoặc cần bổ sung cơ chế theo dõi liên tục.
- ETL chạy lúc `01:00`. File đến sau giờ chạy được xử lý bằng nút **Chạy import ngay** hoặc lịch chạy tiếp theo.
- MVP chưa tự động xóa snapshot. Chính sách retention, ví dụ giữ 90 hoặc 180 ngày, sẽ được bổ sung sau khi hệ thống vận hành ổn định.
- Snapshot phục vụ backup và đối soát nhưng chưa phải rollback tự động. Chức năng **Restore/Reprocess** có kiểm soát sẽ được thiết kế ở giai đoạn sau vì hash đã hoàn tất hiện được ETL bỏ qua.

Các cấu hình ETL Worker trong `.env`:

- `SAP_FILE_PATTERN`: mẫu tên file, mặc định `export*.xlsx`.
- `WEB_UPLOAD_ENABLED`: bật/tắt API upload; `.env.example` mặc định `true`, nhưng endpoint chỉ cho quản trị viên đã đăng nhập.
- `WEB_UPLOAD_MAX_BYTES`: kích thước upload tối đa, mặc định `104857600` byte (100 MB).
- `SAP_WORKSHEET_NAME`: tên worksheet; để trống để đọc worksheet đầu tiên.
- `ETL_DAILY_TIME`: giờ chạy mỗi ngày theo định dạng `HH:MM`, mặc định `01:00`.
- `ETL_TIMEZONE`: múi giờ lịch chạy, mặc định `Asia/Ho_Chi_Minh`.
- `ETL_MIN_FILE_AGE_SECONDS`: thời gian chờ file ổn định trước khi đọc.
- `ETL_ENABLE_SOFT_DELETE`: chỉ đặt `true` khi file là snapshot đầy đủ của Product/Sales Organization; mặc định an toàn trong `.env.example` là `false`.
- `ETL_BATCH_SIZE`: số dòng mỗi lần bulk insert.
- `ETL_RUN_ON_STARTUP`: đặt `true` nếu muốn chạy thêm một lần khi container khởi động.

### Generative AI tạo kế hoạch — Giai đoạn 4/5

AI mặc định bị tắt để hệ thống chạy được mà không cần API key. Cách cấu hình khuyến nghị ở Giai đoạn 5:

1. Mở **Cài đặt** trên Web App.
2. Tạo hoặc đăng nhập tài khoản quản trị.
3. Nhập API key, bấm **Kiểm tra kết nối**, sau đó **Lưu API key**.

API key được mã hóa phía server bằng ASP.NET Data Protection; key ring nằm trong Docker volume `app_keys`. Trình duyệt chỉ nhận trạng thái đã cấu hình và chuỗi che, không nhận lại key gốc. Mật khẩu quản trị được băm PBKDF2; các lần đăng nhập và thay đổi key có audit trong SQL Server.

Cấu hình `.env` dưới đây chỉ còn là phương án tương thích cho môi trường phát triển chưa thiết lập trang quản trị:

```env
AI_ENABLED=true
AI_API_KEY=your-groq-api-key
AI_MODEL=llama-3.3-70b-versatile
AI_MAX_RECORDS=50
AI_REQUESTS_PER_MINUTE=5
```

Khởi động lại Web API sau khi đổi cấu hình:

```powershell
docker compose up -d --build web-api
```

Web App sẽ hiện khối Generative AI với hai chức năng: chuyển câu hỏi tự nhiên thành bộ lọc nháp để người dùng xác nhận, và tạo kế hoạch từ dữ liệu đang lọc. Backend chỉ gửi tối đa `AI_MAX_RECORDS` bản ghi thuộc bộ lọc hiện tại, dùng allowlist trường và mặc định loại `Customer Name` cùng 149 trường chi tiết. API key chỉ nằm ở server; không commit `.env` và không đưa key vào JavaScript.

Kế hoạch AI chỉ là đề xuất, không có quyền sửa SAP, Excel hoặc SQL. Trước khi gửi dữ liệu SAP thật tới dịch vụ bên ngoài phải có chấp thuận của chủ dữ liệu; bản demo nên dùng dữ liệu đã ẩn danh. Xem thiết kế và checklist tại `Ke_hoach_Giai_doan_4_Generative_AI.md`.

## Khởi động

```powershell
docker compose up --build -d
docker compose ps
```

Mở:

- Web App: <http://localhost:8080>
- Health: <http://localhost:8080/api/health>

Web App giai đoạn 3 cung cấp:

- Bộ lọc Product và Sales Organization có giá trị mặc định `12` và `SG50`, nhưng vẫn cho phép thay đổi.
- Lọc theo Business Scenario, SI Status, Sales Office, PlantCode, SI ID, Customer, OIL SC/SO/PO và Created Date.
- Phân trang phía server; danh sách chỉ tải các cột cần thiết.
- Màn hình chi tiết chỉ đọc đầy đủ 149 trường nguồn.
- Màn hình lịch sử import hiển thị Insert/Update/Soft Delete/Unchanged/Error và audit từng bản ghi, từng trường cũ → mới.

Các endpoint chính:

```text
GET /api/sap-data
GET /api/sap-data/filter-options
GET /api/sap-data/{id}
GET /api/import-logs
GET /api/import-logs/{id}
GET /api/import-logs/{id}/changes
POST /api/imports/run
GET /api/imports/status
GET /api/ai/status
POST /api/ai/plans
```

`Created Date` ánh xạ tới `SI Created on`; `OIL SC` ánh xạ tới `OIL Sales`.
Web App không sửa dữ liệu SAP trong MVP.

Kết quả health mong đợi:

```json
{
  "status": "Healthy",
  "database": "SapDataSync"
}
```

## Xem log

```powershell
docker compose logs -f web-api
docker compose logs -f etl-worker
```

Hướng dẫn đầy đủ dành cho người nhận bàn giao nằm tại `docs\HUONG_DAN_VAN_HANH.txt` và được đóng gói trong `Setup.exe`.

## Backup SQL Server

Khi hệ thống đang chạy, tạo backup `COPY_ONLY` có checksum và tự chạy `RESTORE VERIFYONLY`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\backup-database.ps1
```

File `.bak` được lưu trong thư mục backup mặc định của SQL Server host. Cần sao chép backup sang vị trí độc lập và thử restore trên môi trường riêng; `VERIFYONLY` không thay thế cho bài kiểm thử restore đầy đủ.

## SQL Server edition cho production

Edition của SQL Server 2022 do bộ cài SQL bên ngoài quyết định. Express phù hợp deployment nhỏ trong giới hạn sản phẩm; Developer chỉ dùng phát triển/kiểm thử. Standard hoặc Enterprise cần license hợp lệ.

Mỗi file mới đi qua luồng:

```text
Tính SHA-256 → Tạo/tái sử dụng snapshot → Validate 149 cột → SapDataStaging → Insert/Update/Soft Delete SapData → SapDataChangeLog → ImportLog
```

Khóa so sánh là tổ hợp `Shipping Instructions ID` và `Unique Number`. Nếu một thành phần trống, thành phần còn lại vẫn được dùng; nếu cả hai trống hoặc tổ hợp bị trùng, file bị từ chối và transaction được rollback. File có cùng SHA-256 với một lần import thành công sẽ được bỏ qua.

### Audit thay đổi và soft delete

- `Insert`: tạo bản ghi `SapData` và audit gồm SapData ID, khóa nghiệp vụ, dòng Excel và toàn bộ giá trị mới có dữ liệu.
- `Update`: giữ nguyên SapData ID và audit chính xác từng trường với giá trị cũ → mới.
- `Soft Delete`: bản ghi đang hoạt động nhưng không còn trong snapshot mới của cùng Product/Sales Organization được đặt `IsDeleted = 1` và `DeletedAt`; không xóa vật lý.
- Nếu bản ghi soft-deleted xuất hiện lại, hệ thống khôi phục cùng SapData ID và ghi audit Update (`IsDeleted: True → False`).
- Web App mặc định không hiển thị bản ghi đã soft delete trong danh sách dữ liệu SAP; lịch sử vẫn xem được trong chi tiết import.
- `Unchanged` chỉ lưu số lượng tổng hợp, không tạo audit từng dòng.
- Audit bắt đầu từ các lần import mới sau khi migration được áp dụng; ImportLog cũ không được tạo dữ liệu lịch sử giả.

Soft delete chỉ an toàn khi mỗi file là **snapshot đầy đủ** của Product và Sales Organization đang import. Không dùng file trích xuất một phần để đồng bộ theo cơ chế này vì các dòng không có trong file sẽ được xem là đã bị xóa.

ETL Worker không hard-delete dữ liệu khỏi `SapData` và không ghi, đổi tên hoặc di chuyển file Excel trong `data/source`; Worker chỉ tạo bản sao bất biến trong `data/archive`.

### Upload từ Web App

- Web API ghi file `.xlsx` vào named volume `uploads_data`; file vẫn tồn tại khi container được tạo lại.
- Tên file phía người dùng không được dùng làm đường dẫn lưu. Server tạo tên an toàn có timestamp và SHA-256.
- File được giới hạn kích thước, kiểm tra phần mở rộng, chữ ký ZIP/Open XML và chống lưu trùng theo SHA-256.
- ETL mount `data/uploads` ở chế độ read-only, tạo/tái sử dụng snapshot trong `data/archive`, sau đó mới import.
- Nút **Upload & Import** upload xong sẽ yêu cầu ETL chạy ngay. File đã hoàn tất trước đó vẫn được ImportLog nhận diện và bỏ qua.
- Khi public Internet, phải để `WEB_UPLOAD_ENABLED=false` cho đến khi có authentication, authorization, HTTPS, giới hạn tần suất và kiểm tra malware. Phiên bản hiện tại phù hợp để chạy nội bộ/test, chưa phải cổng upload công khai ẩn danh.

### Chuyển dữ liệu bind mount cũ sang named volume

Sau khi đổi từ bind mount sang named volume, Docker không tự sao chép các file đang có trong `data/uploads` và `data/archive`. Trước lần khởi động đầu tiên với cấu hình mới, tạo volume và sao chép dữ liệu một lần:

```powershell
docker compose create
docker run --rm --user 0:0 --entrypoint sh -v "${PWD}/data/uploads:/source:ro" -v "sap-datasync_uploads_data:/target" sap-datasync-etl-worker -c "cp -a /source/. /target/ && chown -R 1654:1654 /target"
docker run --rm --user 0:0 --entrypoint sh -v "${PWD}/data/archive:/source:ro" -v "sap-datasync_archive_data:/target" sap-datasync-etl-worker -c "cp -a /source/. /target/ && chown -R 10001:10001 /target"
docker compose up -d --build
```

`docker compose down` không xóa các volume này. Không chạy `docker compose down -v` nếu muốn giữ database, file upload và snapshot. Named volume là lưu trữ bền vững trên một Docker host, nhưng không thay thế cho backup.

### Định hướng rollback từ archive

Không cho rollback tự do. Phương án đề xuất là chức năng quản trị **Restore snapshot**:

1. Chọn snapshot trong `data/archive`.
2. Preview số Insert/Update/Soft Delete và các xung đột với dữ liệu mới hơn.
3. Yêu cầu xác nhận rõ ràng.
4. Chạy như một ImportLog mới có `OperationType = Restore` và liên kết snapshot nguồn.
5. Không xóa hoặc sửa lịch sử import cũ.

True rollback theo từng ImportLog có thể dùng `SapDataChangeLog` để áp dụng thay đổi ngược, nhưng phải chặn nếu bản ghi đã được một import mới hơn cập nhật. Chức năng này chưa được bật trong MVP hiện tại.

Chạy import một lần để kiểm tra:

```powershell
docker compose run --rm `
  -e ETL_RUN_ONCE=true `
  -e ETL_MIN_FILE_AGE_SECONDS=0 `
  etl-worker
```

## Dừng hệ thống

```powershell
docker compose down
```

Lệnh trên không xóa database volume. Không chạy `docker compose down -v` nếu muốn giữ database.

## Build .NET ngoài Docker

```powershell
dotnet restore SapDataSync.sln --configfile NuGet.Config
dotnet build SapDataSync.sln --configuration Release --no-restore
```

Build riêng Python ETL Worker:

```powershell
docker compose build etl-worker
```

## Kiểm tra Giai đoạn 1

Khi các container đang chạy và cấu hình SQL Server host trong `.env` đã chính xác:

```powershell
.\tests\verify-stage1.ps1
```

Script kiểm tra build .NET, cấu hình Compose, API/database health, mount Excel read-only và schema đủ 149 cột nguồn.

## Kiểm tra Giai đoạn 2

Sau khi file mẫu đã được import và các container đang chạy:

```powershell
$env:SQL_PASSWORD = "mật-khẩu-trong-file-env"
.\tests\verify-stage2.ps1
```

Script xác nhận build, health, 149 cột, 9.726 dòng trong bảng chính, chống import lặp, audit Insert/Update/Soft Delete và SHA-256 của Excel không thay đổi. Các phép thử thay đổi dữ liệu chạy trong transaction và rollback.

## Kiểm tra Giai đoạn 3

Khi các container đang chạy:

```powershell
.\tests\verify-stage3.ps1
```

Script xác nhận Web App, phân trang, bộ lọc metadata, chi tiết đủ 149 trường, lịch sử import và lỗi HTTP 400 cho tham số không hợp lệ.

## Kiểm tra Giai đoạn 4

Không cần API key thật. Bộ kiểm thử tạo một AI Provider giả lập tạm thời trong mạng Docker để kiểm tra kế hoạch JSON, bộ lọc ngôn ngữ tự nhiên, prompt injection, schema sai, HTTP 429, timeout và rate limit. Test cũng xác nhận payload giới hạn 50 dòng, không có `Customer Name`, đồng thời so sánh dấu vân tay Staging/SapData/ImportLog/ChangeLog và SHA-256 của Excel trước/sau. Sau khi chạy, script dừng mock và khôi phục Web API về cấu hình `.env`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\verify-stage4.ps1
```

Kiểm thử bằng Groq thật chỉ thực hiện sau khi người nhận tự đặt `AI_API_KEY` trong `.env` và dùng dữ liệu demo/đã ẩn danh.

Sau khi đã cấu hình key local, chạy bài kiểm thử live tối thiểu sau. Script chỉ chọn một Shipping Instruction, không hiển thị API key và vẫn kiểm tra database/file Excel không thay đổi:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\verify-stage4-live.ps1
```

### Chạy import thủ công

1. Đặt file Excel cần kiểm tra vào `data/source` với tên khớp `SAP_FILE_PATTERN`.
2. Kiểm tra `SAP_PRODUCT` và `SAP_SALES_ORGANIZATION` trong `.env`.
3. Mở **Lịch sử import** trên Web App.
4. Chọn **Chạy import ngay** và xác nhận.

Web App sẽ hiển thị trạng thái và tự làm mới lịch sử sau khi ETL hoàn tất. File đã import thành công trước đó được nhận diện bằng SHA-256 và tự động bỏ qua; file Excel luôn được đọc ở chế độ chỉ đọc.

Kiểm tra tự động chức năng này:

```powershell
.\tests\verify-manual-import.ps1
```

## Cấu trúc chính

```text
src/WebApi
src/EtlWorker
src/Importer      # bản C# tham chiếu, không chạy trong Compose
database/scripts
docker
compose.yaml
```

## Trạng thái roadmap

- [x] Giai đoạn 1: nền tảng Docker và database.
- [x] Giai đoạn 2A: hoàn thành và kiểm chứng luồng import bằng C#/.NET.
- [x] Giai đoạn 2B: chuyển sang Python ETL Worker và kiểm thử tương đương.
- [x] Giai đoạn 3: Web API và Web App dữ liệu.
- [ ] Giai đoạn 4: đã hoàn tất code 4A/4B, kiểm thử mock và Groq thật; chờ kiểm tra giao diện thực tế và người dùng nghiệp vụ nghiệm thu 10 tình huống mẫu.
- [x] Giai đoạn 5: nghiệm thu kỹ thuật, launcher, Setup.exe, quản trị API key, backup và tài liệu bàn giao; còn ký số bản phát hành và UAT trực quan của người dùng nghiệp vụ trước production.
