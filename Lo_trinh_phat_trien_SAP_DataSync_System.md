# Lộ trình phát triển SAP DataSync System — MVP

## 1. Mục tiêu cuối cùng

```text
SAP
↓
export.xlsx hoặc export_YYYYMMDD_HHmmss.xlsx
↓
Snapshot bất biến trong data/archive
↓
Python ETL Worker chạy mỗi ngày
↓
SQL Staging
↓
So sánh với bảng chính
↓
Insert mới / Update thay đổi / Soft Delete
↓
Import Log
↓
Web App xem dữ liệu
↓
AI tự động tạo kế hoạch — nếu thực hiện được
```

Hệ thống được đóng gói bằng Docker. Người dùng cuối chỉ truy cập bằng trình duyệt.

## 2. Phạm vi thực hiện

### Bắt buộc

- Đọc các file `export*.xlsx` tự động một lần mỗi ngày, hỗ trợ cả tên cố định và tên có timestamp.
- Tạo snapshot bất biến theo SHA-256 trong `data/archive` trước khi import; không sửa file nguồn.
- Không chỉnh sửa file Excel.
- Kiểm tra đúng cấu trúc 149 cột.
- Import dữ liệu vào Staging.
- So sánh với bảng chính.
- Insert dữ liệu mới.
- Update dữ liệu thay đổi.
- Soft delete dữ liệu không còn trong snapshot đầy đủ cùng Product/Sales Organization.
- Audit Insert/Update/Soft Delete theo SapData ID và từng trường cũ → mới.
- Lưu lịch sử và kết quả import.
- Web App hiển thị và tra cứu dữ liệu.
- Chạy bằng Docker Compose.

### Tùy chọn

- Generative AI tạo kế hoạch dựa trên dữ liệu.

### Chưa thực hiện trong MVP

- Kubernetes.
- Microservice phức tạp.
- Message queue.
- Dashboard nâng cao.
- Phân quyền doanh nghiệp nhiều cấp.
- CI/CD và monitoring nâng cao.
- Đồng bộ ngược về SAP.
- AI tự động sửa dữ liệu hoặc thực hiện hành động nghiệp vụ.
- Tự động chuyển/xóa snapshot theo thời hạn retention.
- Restore/Reprocess có kiểm soát để phục hồi từ snapshot Excel đã import.

## 3. Kiến trúc MVP

```text
Docker Compose
├── web-api
│   └── ASP.NET Core Web App và Web API
├── etl-worker
│   └── Python tự động đọc và đồng bộ Excel
├── ai-service — bổ sung ở Giai đoạn 4
│   └── Python Generative AI tạo kế hoạch
└── sqlserver
    ├── SapDataStaging
    ├── SapData
    └── ImportLog
```

## 4. Giai đoạn 1 — Nền tảng Docker và database

### Công việc

- [x] Tạo project `WebApi`.
- [x] Tạo project `Importer` .NET làm bản triển khai và kiểm chứng ban đầu.
- [x] Tạo script kiểm tra Giai đoạn 1.
- [x] Tạo Dockerfile cho Web/API và Importer.
- [x] Tạo `compose.yaml`.
- [x] Thêm SQL Server container.
- [x] Tạo persistent volume cho SQL Server.
- [x] Mount thư mục Excel vào Importer ở chế độ read-only.
- [x] Tạo `.dockerignore` và `.env.example`.
- [x] Tạo endpoint `GET /api/health`.
- [x] Tạo script database.

### Bảng SQL tối thiểu

```text
SapDataStaging
SapData
ImportLog
```

### Kết quả cần đạt

```powershell
docker compose up --build -d
docker compose ps
```

- Ba container chạy được.
- Web/API kết nối được SQL Server.
- Database không mất khi container được tạo lại.
- File Excel local không được commit lên Git.

## 5. Giai đoạn 2 — Import Excel và đồng bộ SQL

Đây là giai đoạn quan trọng nhất.

Phiên bản C#/.NET đã được dùng làm mốc kiểm chứng. Python ETL Worker hiện đã thay thế container C# và vượt qua cùng bộ kiểm thử. Hai worker không chạy đồng thời.

### Công việc

- [x] Python ETL Worker chạy theo lịch một lần mỗi ngày.
- [x] Đọc `.xlsx` ở chế độ chỉ đọc.
- [x] Tính file hash.
- [x] Kiểm tra file đã import hay chưa.
- [x] Kiểm tra worksheet và 149 cột.
- [x] Tạo bản ghi ImportLog.
- [x] Đọc từng dòng Excel.
- [x] Import dữ liệu vào `SapDataStaging`.
- [x] So sánh với `SapData`.
- [x] Insert bản ghi mới.
- [x] Update bản ghi thay đổi.
- [x] Không update bản ghi không thay đổi.
- [x] Cập nhật kết quả ImportLog.
- [x] Không ghi hoặc sửa file Excel.

### Logic tổng quát

```text
File mới
→ Validate
→ Staging
→ So sánh khóa và row hash
→ Insert / Update / Soft Delete / Unchanged
→ Log kết quả
```

### Kiểm thử

- [x] Import đủ 9.726 dòng.
- [x] Import đủ 149 cột.
- [x] Đối chiếu số dòng, số cột và row hash giữa Staging/bảng chính.
- [x] Import lại cùng file không tạo dữ liệu trùng không mong muốn.
- [x] Bản ghi thay đổi được update đúng.
- [x] File sai cấu trúc bị từ chối, rollback và có log.
- [x] Excel không thay đổi sau import.

### Kết quả cần đạt

- Luồng Excel → Staging → SapData → ImportLog chạy hoàn chỉnh.
- Có thể truy vết dữ liệu về file và số dòng Excel.

### Chuyển đổi sang Python ETL Worker

- [x] Tạo `src/EtlWorker` bằng Python.
- [x] Tạo Dockerfile riêng cho ETL Worker.
- [x] Dùng APScheduler để chạy đúng một lần mỗi ngày.
- [x] Đọc `.xlsx` streaming bằng `openpyxl(read_only=True)`.
- [x] Dùng `pyodbc` để bulk insert vào Staging.
- [x] Giữ nguyên Business Key, file hash, row hash và mở rộng quy tắc Insert/Update/Soft Delete/Unchanged.
- [x] Ghi `SapDataChangeLog` cho Insert, Update và Soft Delete.
- [x] Đưa logic đồng bộ SQL vào stored procedure dùng chung.
- [x] Giữ mount Excel read-only và không thay đổi hai file nguồn.
- [x] Chạy lại toàn bộ kiểm thử 9.726 dòng, 149 cột, update, chống trùng và rollback.
- [x] Chỉ thay container Importer C# sau khi Python đạt kết quả tương đương.
- [x] Không chạy C# Importer và Python ETL Worker đồng thời.

## 6. Giai đoạn 3 — Web API và Web App

### Web API tối thiểu

```text
GET /api/health
GET /api/sap-data
GET /api/sap-data/{id}
GET /api/import-logs
GET /api/import-logs/{id}
```

### Công việc Web API

- [x] Phân trang dữ liệu.
- [x] Tìm kiếm cơ bản.
- [x] Lọc theo các trường quan trọng.
- [x] Lấy chi tiết bản ghi.
- [x] Lấy danh sách và chi tiết lịch sử import.
- [x] Chuẩn hóa response lỗi.

### Màn hình Web App

#### Danh sách dữ liệu SAP

- [x] Hiển thị dạng bảng.
- [x] Phân trang.
- [x] Tìm kiếm.
- [x] Xem chi tiết.

#### Lịch sử import

- [x] Hiển thị tên file và thời gian import.
- [x] Hiển thị trạng thái.
- [x] Hiển thị số dòng Insert, Update, Soft Delete, Unchanged và Error.
- [x] Hiển thị audit theo SapData ID, dòng Excel và trường cũ → mới.
- [x] Hiển thị thông báo lỗi nếu có.

#### Import thủ công — nếu cần

- [x] Nút yêu cầu ETL Worker chạy ngay.
- [x] Upload bản sao `.xlsx` từ Web App vào `data/uploads` và yêu cầu ETL chạy ngay.
- [x] Kiểm tra kích thước, định dạng Open XML và chống trùng upload theo SHA-256.
- [ ] Bổ sung authentication/authorization trước khi bật upload trên Internet công khai.
- [ ] Restore snapshot có Preview, kiểm tra xung đột và audit quản trị.

### Kết quả cần đạt

- Người dùng truy cập hệ thống bằng trình duyệt.
- Người dùng xem và tìm kiếm được dữ liệu.
- Người dùng kiểm tra được lịch sử import.
- Web App không cung cấp chức năng sửa dữ liệu SAP trong MVP.

## 7. Giai đoạn 4 — Generative AI tạo kế hoạch

Chỉ thực hiện sau khi ba giai đoạn đầu hoạt động ổn định.

### Use case

```text
Người dùng chọn hoặc lọc dữ liệu
↓
Nhấn Tạo kế hoạch
↓
Web API lấy dữ liệu liên quan
↓
Gọi Python AI Service
↓
AI Service gọi AI Provider
↓
Hiển thị kế hoạch đề xuất
```

### Công việc

- [ ] Chọn AI Provider.
- [ ] Xác định dữ liệu được phép gửi cho AI.
- [ ] Tạo endpoint tạo kế hoạch.
- [ ] Tạo Python AI Service và Dockerfile riêng.
- [ ] Thiết kế prompt theo mục tiêu nghiệp vụ.
- [ ] Lưu API key ngoài Git.
- [ ] Hiển thị rõ nội dung do AI tạo.
- [ ] Không cho AI trực tiếp sửa dữ liệu.
- [ ] Kiểm tra chất lượng kế hoạch với người dùng.

### Không cần trong giai đoạn này

- Vector database.
- RAG phức tạp.
- AI agent tự hành động.
- Tách thêm nhiều AI service khi một service Python đã đáp ứng đủ nhu cầu.

### Kết quả cần đạt

- AI tạo được một bản kế hoạch từ dữ liệu người dùng đã chọn.
- Kết quả chỉ là đề xuất để người dùng xem xét.

## 8. Giai đoạn 5 — Kiểm thử, đóng gói và bàn giao

### Công việc

- [ ] Kiểm thử lại toàn bộ luồng bằng Docker Compose.
- [ ] Kiểm thử database volume.
- [ ] Kiểm thử Excel read-only mount.
- [ ] Kiểm thử import lần đầu và import lại.
- [ ] Kiểm thử Web App từ máy người dùng.
- [ ] Kiểm thử AI nếu có.
- [ ] Tạo `.env.example` đầy đủ.
- [ ] Viết README hướng dẫn chạy.
- [ ] Viết hướng dẫn đặt file Excel.
- [ ] Viết hướng dẫn xem log.
- [ ] Viết hướng dẫn backup SQL Server cơ bản.
- [ ] Xác nhận SQL Server license trước production.
- [ ] Bàn giao source code và tài liệu.

### Kết quả cần đạt

Người nhận bàn giao có thể:

1. Cấu hình `.env`.
2. Chạy `docker compose up --build -d`.
3. Đặt file Excel vào thư mục nguồn.
4. Kiểm tra ImportLog.
5. Mở Web App bằng trình duyệt.
6. Xem dữ liệu đã import.
7. Dừng và chạy lại hệ thống mà không mất database.

## 9. Thứ tự thực hiện

```text
Giai đoạn 1
Docker + SQL Server + database
↓
Giai đoạn 2
Python ETL: Excel → Staging → Insert/Update/Soft Delete → Audit Log
↓
Giai đoạn 3
Web API + Web App xem dữ liệu
↓
Giai đoạn 4
AI tạo kế hoạch — tùy chọn
↓
Giai đoạn 5
Kiểm thử + đóng gói + bàn giao
```

Không bắt đầu AI trước khi luồng import và Web App đã ổn định.

## 10. Definition of Done cho MVP

Các kết quả ETL đã được chạy lại và xác nhận bằng Python ETL Worker.

- [x] Toàn bộ hệ thống chạy bằng Docker Compose.
- [ ] Người dùng cuối chỉ cần trình duyệt.
- [x] File Excel được đọc ở chế độ chỉ đọc.
- [x] File Excel không xuất hiện trong Git repository.
- [x] Import đủ 149 cột.
- [x] Import đủ 9.726 dòng từ file mẫu.
- [x] Dữ liệu được đưa vào Staging.
- [x] Dữ liệu mới được insert vào bảng chính.
- [x] Dữ liệu thay đổi được update.
- [x] Dữ liệu không thay đổi không bị update lại.
- [x] Mỗi lần import có lịch sử và kết quả.
- [x] Web App hiển thị và tìm kiếm được dữ liệu.
- [x] Database không mất khi container được tạo lại.
- [x] Có README hướng dẫn chạy hiện tại.
- [ ] AI tạo được kế hoạch nếu phần tùy chọn được thực hiện.

## 11. Công việc tiếp theo

**Giai đoạn 3 — Web API và Web App xem dữ liệu** đã hoàn thành:

1. API danh sách và chi tiết dữ liệu SAP, phân trang phía server.
2. API danh sách và chi tiết ImportLog.
3. Bộ lọc nghiệp vụ với Product `12` và Sales Organization `SG50` là giá trị mặc định có thể thay đổi.
4. Giao diện responsive xem dữ liệu, đủ 149 trường chi tiết và lịch sử import.
5. Bộ kiểm thử Giai đoạn 3 trên 9.726 bản ghi.

Công việc kế tiếp là người dùng chạy thử MVP, sau đó mới quyết định triển khai Giai đoạn 4 (AI) và nhóm tính năng tác vụ như Reminder/Auto E-Mail.
