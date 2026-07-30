# Kế hoạch triển khai MVP: SAP → SQL Server → Web App → AI

## 1. Mục tiêu triển khai

Xây dựng phiên bản MVP thực hiện được toàn bộ luồng:

```text
SAP
→ export.xlsx hoặc export_YYYYMMDD_HHmmss.xlsx
→ Snapshot bất biến trong data/archive
→ Python ETL Worker chạy mỗi ngày
→ SQL Staging
→ So sánh bảng chính
→ Insert / Update / Soft Delete
→ Import Log + SapDataChangeLog
→ Web App xem dữ liệu
→ AI tạo kế hoạch — tùy chọn
```

Ưu tiên của dự án:

1. Luồng import Excel chạy đúng.
2. Dữ liệu Staging và bảng chính chính xác.
3. Insert/Update/Soft Delete hoạt động ổn định và có audit.
4. Lịch sử import có thể kiểm tra được.
5. Web App hiển thị được dữ liệu.
6. AI được bổ sung nếu phần chính đã hoàn thành.

## 2. Nguyên tắc dữ liệu nguồn

- ETL chấp nhận cả `export.xlsx` bị SAP ghi đè và file riêng theo dạng `export_YYYYMMDD_HHmmss.xlsx`.
- Trước khi import, mỗi nội dung mới được sao lưu bất biến vào `data/archive` theo SHA-256.
- `work flow locate 28072026.xlsx` là tài liệu tham chiếu cấu trúc.
- Hai file chỉ được đọc, không chỉnh sửa.
- Không thay đổi tên, thứ tự hoặc giá trị các cột trong Excel.
- Không tự sửa ô trống, `0`, `N/A` hoặc ngày tháng bất thường.
- Không commit file Excel lên Git.
- Thư mục Excel được mount vào container ETL Worker ở chế độ read-only.

## 3. Kiến trúc Docker tối giản

Phần bắt buộc cần ba container; khi bổ sung AI sẽ có container thứ tư:

```text
Docker Compose
├── web-api        ASP.NET Core
├── etl-worker     Python
├── ai-service     Python — tùy chọn, bổ sung sau
└── sqlserver
```

### `web-api`

Chứa:

- Web App.
- Web API.
- Chức năng xem và tìm kiếm dữ liệu.
- Chức năng xem lịch sử import.
- Gọi AI Service khi chức năng AI được triển khai.

### `etl-worker`

Chứa Python ETL Worker chạy độc lập:

- Chạy tự động một lần mỗi ngày bằng lịch cấu hình.
- Đọc Excel ở chế độ chỉ đọc.
- Kiểm tra cấu trúc 149 cột.
- Ghi dữ liệu vào Staging.
- Thực hiện Insert/Update/Soft Delete vào bảng chính và ghi audit từng bản ghi.
- Ghi lịch sử xử lý.

Công nghệ dự kiến:

- APScheduler với cron trigger để chạy đúng giờ.
- `openpyxl` ở chế độ `read_only=True` để đọc Excel streaming.
- `pyodbc` với bulk insert để ghi SQL Server.
- `hashlib` để tính file hash và row hash.
- Pandas hoặc Polars chỉ dùng khi có biến đổi dữ liệu phức tạp.
- SQLAlchemy là tùy chọn, không bắt buộc cho bulk import.

### `ai-service` — tùy chọn

- Viết bằng Python và chạy trong container riêng.
- Nhận dữ liệu cần thiết từ ASP.NET Core Web API.
- Gọi AI Provider và trả về kế hoạch đề xuất.
- Không trực tiếp sửa Excel hoặc dữ liệu nghiệp vụ.

### `sqlserver`

Chứa:

- Bảng Staging.
- Bảng dữ liệu chính.
- Bảng lịch sử import.
- Persistent volume để giữ database khi container được tạo lại.

## 4. Cấu trúc source code dự kiến

Giữ cấu trúc đơn giản, không áp dụng kiến trúc phân lớp sâu khi chưa cần:

```text
Project1/
├── src/
│   ├── WebApi/
│   ├── EtlWorker/
│   ├── Importer/        # bản C# tham chiếu, không chạy trong Compose
│   └── AiService/       # bổ sung ở giai đoạn AI
├── tests/
├── database/
│   └── scripts/
├── docker/
├── compose.yaml
├── .dockerignore
├── .env.example
└── README.md
```

Web/API giữ ASP.NET Core. ETL và AI dùng Python nhưng nằm ở các container độc lập để có thể thay đổi thư viện mà không ảnh hưởng Web App.

Trạng thái hiện tại: Python ETL Worker đã thay container C# trong Docker Compose và vượt qua cùng bộ kiểm thử. Web API và Web App giai đoạn 3 đã hoàn thành với phân trang, tìm kiếm, lọc, chi tiết 149 trường và lịch sử import. Importer C# chỉ còn là source tham chiếu và không chạy đồng thời.

## 5. Database tối thiểu

### 5.1. `SapDataStaging`

- Chứa đủ 149 trường của file Excel.
- Lưu dữ liệu của lần import đang xử lý.
- Có `ImportId`.
- Có `SourceRowNumber` để truy vết dòng Excel.

### 5.2. `SapData`

- Là bảng dữ liệu chính.
- Có khóa chính nội bộ.
- Lưu thông tin lần import tạo hoặc cập nhật bản ghi.
- Có thể lưu row hash để phát hiện thay đổi.

### 5.3. `ImportLog`

Lưu thông tin mỗi lần import:

- ID lần import.
- Tên file.
- File hash.
- Thời điểm bắt đầu và kết thúc.
- Trạng thái.
- Tổng số dòng đọc.
- Số dòng insert.
- Số dòng update.
- Số dòng soft delete.
- Số dòng không thay đổi.
- Số dòng lỗi.
- Thông báo lỗi nếu có.

Có thể bổ sung `ImportError` sau nếu cần lưu lỗi chi tiết theo từng dòng và cột.

### 5.4. `SapDataChangeLog`

- Lưu `Insert`, `Update`, `Delete` (soft delete) theo từng SapData ID.
- Lưu khóa nghiệp vụ, dòng Excel, JSON giá trị trước và sau.
- API so sánh JSON và chỉ trả các trường thực sự thay đổi cho Web App.
- ImportLog cũ trước migration không được backfill giả lập.

## 6. Luồng Python ETL Worker

```text
APScheduler kích hoạt một lần mỗi ngày
↓
Tính file hash
↓
Kiểm tra file đã được import chưa
↓
Kiểm tra worksheet và 149 cột
↓
Tạo ImportLog với trạng thái Processing
↓
Đọc Excel theo từng dòng
↓
Import vào SapDataStaging
↓
So sánh với SapData
↓
Insert mới / Update thay đổi / Soft Delete dòng bị thiếu / Bỏ qua dòng không đổi
↓
Cập nhật ImportLog
```

Nếu file sai cấu trúc:

- Dừng import.
- Không sửa file.
- Ghi trạng thái Failed và nội dung lỗi vào ImportLog.

## 7. Logic so sánh

- Sử dụng khóa nghiệp vụ để tìm bản ghi tương ứng.
- Khóa được triển khai là tổ hợp `Shipping Instructions ID` và `Unique Number`.
- Nếu một thành phần khóa trống, thành phần còn lại vẫn được dùng; nếu cả hai trống hoặc tổ hợp bị trùng trong cùng file, toàn bộ lần import bị rollback.
- Giá trị khóa trống chỉ được xử lý trong phép so sánh, không thay đổi dữ liệu nguồn.
- Tính row hash để xác định dữ liệu có thay đổi hay không.

Quy tắc:

| Trạng thái | Hành động |
|---|---|
| Chưa có khóa tương ứng | Insert |
| Có khóa, hash khác | Update |
| Có khóa, hash giống | Không cập nhật |
| Không còn trong snapshot đầy đủ cùng Product/Sales Organization | Soft Delete |
| Bản ghi soft-deleted xuất hiện lại | Khôi phục cùng ID và ghi Update |

Không hard-delete dữ liệu khỏi bảng chính. Soft delete chỉ được áp dụng khi file là snapshot đầy đủ của đúng phạm vi Product/Sales Organization.

`ETL_ENABLE_SOFT_DELETE` mặc định là `false` trong file cấu hình mẫu. Chỉ đặt `true` sau khi xác nhận file SAP là snapshot đầy đủ; giá trị bật/tắt được lưu vào từng `ImportLog` để audit.

## 8. Import tự động

Phương án đơn giản:

- Python ETL Worker chạy một lần mỗi ngày theo giờ cấu hình.
- Thư mục nguồn được mount read-only.
- ETL Worker dùng file hash và ImportLog để tránh xử lý lại cùng file.
- ETL Worker không di chuyển, đổi tên hoặc ghi đè file nguồn.

### Chính sách file nguồn và phục hồi

- SAP có thể tiếp tục xuất `export.xlsx`; nếu có thể thay đổi tác vụ export, tên riêng như `export_20260730_010000.xlsx` vẫn là lựa chọn tốt hơn.
- `data/source` được mount read-only. `data/archive` là bind mount riêng có quyền ghi cho ETL.
- File người dùng upload được Web API lưu bền vững tại `data/uploads`; ETL chỉ mount thư mục upload ở chế độ read-only.
- Sau khi file đủ tuổi tối thiểu, ETL tính SHA-256, sao chép nguyên trạng sang archive bằng file tạm và chỉ công bố snapshot `.xlsx` sau khi hash khớp.
- ETL import từ snapshot thay vì file nguồn. Nếu việc sao chép thất bại hoặc nguồn thay đổi trong lúc sao chép, lần import dừng an toàn.
- Cùng SHA-256 tái sử dụng snapshot đã có và tiếp tục được `ImportLog` nhận diện để không đồng bộ trùng.
- SAP nên ghi vào file tạm `.tmp` và chỉ đổi tên thành `.xlsx` sau khi hoàn tất để giảm khả năng ETL gặp file đang ghi dở.
- Lịch `01:00` quét tất cả file khớp `export*.xlsx`.
- File đến sau `01:00` được xử lý bằng nút **Chạy import ngay** hoặc ở lịch chạy kế tiếp.
- Nếu SAP ghi đè nhiều lần giữa hai lượt quét, chỉ phiên bản hiện diện lúc quét được snapshot; giữ mọi lần export đòi hỏi tên riêng hoặc cơ chế theo dõi liên tục.
- MVP chưa tự động xóa snapshot. Retention là hạng mục giai đoạn sau.
- Snapshot phục vụ backup và đối soát nhưng chưa tạo thành rollback tự động. Cần bổ sung luồng **Restore/Reprocess** có phân quyền và audit để chủ động nhập lại snapshot đã xử lý.

Ví dụ mount:

```yaml
services:
  etl-worker:
    volumes:
      - ${SAP_SOURCE_PATH}:/app/data/source:ro
      - ${SAP_ARCHIVE_PATH}:/app/data/archive
```

Nếu cần demo nhanh, hệ thống có thể hỗ trợ chạy import bằng lệnh thủ công trước, sau đó bật lịch tự động.

## 9. Web API tối thiểu

```text
GET /api/health
GET /api/sap-data
GET /api/sap-data/{id}
GET /api/import-logs
GET /api/import-logs/{id}
```

Yêu cầu:

- `sap-data` có phân trang.
- Có tìm kiếm cơ bản.
- Có thể lọc theo một số trường quan trọng.
- Không tải toàn bộ dữ liệu trong một request.
- API chỉ đọc dữ liệu nghiệp vụ; không cần API sửa dữ liệu trong MVP.

API chạy import thủ công là tùy chọn:

```text
POST /api/imports/run
GET /api/imports/status
```

Chức năng này đã được triển khai bằng nút **Chạy import ngay** trên Web App. ETL Worker chống chạy đồng thời, giữ file nguồn ở chế độ chỉ đọc và tiếp tục bỏ qua file có SHA-256 đã hoàn tất.

## 10. Web App tối thiểu

### Màn hình dữ liệu SAP

- Hiển thị danh sách dữ liệu.
- Phân trang.
- Tìm kiếm cơ bản.
- Xem chi tiết một bản ghi.

### Màn hình lịch sử import

- Hiển thị thời gian import.
- Hiển thị tên file.
- Hiển thị trạng thái.
- Hiển thị số dòng Insert, Update, Soft Delete, Unchanged và Error.
- Hiển thị SapData ID, SI ID, Unique Number, dòng Excel và từng giá trị cũ → mới.
- Hiển thị thông báo lỗi nếu import thất bại.

### Chức năng import thủ công

Chỉ bổ sung nếu người dùng cần:

- Nút chạy import.
- Upload bản sao `.xlsx` từ Web App để lưu tại `data/uploads` và chạy import ngay.
- Upload công khai chỉ được bật sau khi có xác thực, phân quyền, HTTPS, rate limit và kiểm tra malware.
- Restore từ archive phải có Preview/xác nhận/xung đột và tạo ImportLog mới; không cho rollback tự do hoặc sửa lịch sử cũ.

Không cung cấp chức năng sửa dữ liệu Excel hoặc sửa dữ liệu SAP trong MVP.

## 11. Generative AI — tùy chọn

Mục tiêu AI là tự động tạo kế hoạch dựa trên dữ liệu.

Luồng đơn giản:

```text
Người dùng lọc hoặc chọn dữ liệu
↓
Nhấn Tạo kế hoạch
↓
Web API lấy và giới hạn dữ liệu cần thiết từ SQL
↓
Gọi Python AI Service
↓
AI Service gọi AI Provider
↓
Hiển thị kế hoạch do AI đề xuất
```

Nguyên tắc:

- AI chỉ nhận dữ liệu cần thiết.
- AI không chỉnh sửa Excel.
- AI không tự động update hoặc delete SQL.
- Kế hoạch AI là nội dung đề xuất để người dùng xem xét.
- API key được lưu trong environment variable hoặc secret, không commit Git.

MVP không cần:

- Vector database.
- RAG phức tạp.
- AI agent tự thực hiện hành động.
- Nhiều AI service hoặc hạ tầng điều phối phức tạp.

## 12. Kiểm thử bắt buộc

### Docker

- Build được các image.
- `docker compose up` khởi động đủ ba container bắt buộc; bốn container khi bật AI.
- Web/API kết nối được SQL Server.
- Database không mất khi container được tạo lại.
- Excel source được mount read-only.

### Python ETL Worker

- Đọc đủ 149 cột.
- Import đủ 9.726 dòng từ file mẫu.
- Dữ liệu trong SQL khớp Excel.
- Import lại cùng file không tạo dữ liệu trùng không mong muốn.
- Bản ghi thay đổi được update đúng.
- File sai cấu trúc bị từ chối và có log.
- Excel không thay đổi sau khi import.

### Web App

- Hiển thị được danh sách dữ liệu.
- Phân trang và tìm kiếm hoạt động.
- Hiển thị được lịch sử import.
- Người dùng chỉ cần trình duyệt.

### AI nếu được triển khai

- Tạo được kế hoạch từ tập dữ liệu đã chọn.
- Không làm thay đổi dữ liệu SQL.
- Thông báo rõ nội dung do AI tạo.

## 13. Triển khai và bàn giao tối thiểu

Cần bàn giao:

- Source code.
- `compose.yaml`.
- `.env.example`.
- Database script.
- README hướng dẫn chạy.
- Hướng dẫn đặt file Excel nguồn.
- Hướng dẫn xem log.
- Hướng dẫn backup database cơ bản.

Lệnh vận hành chính:

```powershell
docker compose up --build -d
docker compose ps
docker compose logs -f
docker compose down
```

Trước khi sử dụng production vẫn phải xác nhận license SQL Server phù hợp và thiết lập backup database.

## 14. Các phần mở rộng sau MVP

Chỉ thực hiện khi có yêu cầu mới:

- Authentication doanh nghiệp.
- Phân quyền nhiều cấp.
- Dashboard và biểu đồ nâng cao.
- Message queue.
- Monitoring và cảnh báo tập trung.
- CI/CD hoàn chỉnh.
- Nhiều môi trường Docker Compose.
- Kubernetes hoặc triển khai đa máy chủ.
- Đồng bộ ngược về SAP.

## 15. Tiêu chí hoàn thành MVP

Các mục ETL dưới đây đã được xác nhận lại bằng Python ETL Worker sau khi chuyển đổi từ bản C#/.NET.

- [x] Chạy được bằng Docker Compose.
- [ ] Người dùng chỉ cần trình duyệt.
- [x] Excel được đọc ở chế độ chỉ đọc.
- [x] File Excel không được commit lên Git.
- [x] Dữ liệu được import vào Staging.
- [x] Insert dữ liệu mới hoạt động.
- [x] Update dữ liệu thay đổi hoạt động.
- [x] Dòng không thay đổi không bị update lại.
- [x] Có lịch sử của mỗi lần import.
- [x] Web App hiển thị và tìm kiếm được dữ liệu.
- [ ] Có tài liệu hướng dẫn chạy và bàn giao.
- [ ] AI tạo được kế hoạch nếu phần tùy chọn được triển khai.
