# Kế hoạch triển khai Docker-first: SAP → SQL Server → Web App → AI

## 1. Quyết định kiến trúc

Dự án được triển khai theo hướng **Docker-first** và chạy tập trung trên một máy chủ.

- Docker đóng gói Web App, Web API, Import Worker và SQL Server thành các service độc lập.
- Docker Compose quản lý service, network, volume và cấu hình chạy hệ thống.
- Người dùng cuối không phải cài Docker, SQL Server, .NET hoặc Node.js.
- Người dùng truy cập hệ thống bằng trình duyệt qua một URL nội bộ hoặc URL HTTPS.
- Docker Desktop chỉ dùng trên máy phát triển khi cần; không cài trên từng máy người dùng.
- AI được thiết kế thành một service riêng và bổ sung ở giai đoạn sau.

Mô hình sử dụng:

```text
Máy người dùng
└── Chrome / Edge
        │
        ▼
Web App trên máy chủ Docker
        │
        ▼
Web API → SQL Server
        │
        ├── Import Worker → Excel nguồn chỉ đọc
        │
        └── AI Service → AI Provider (giai đoạn sau)
```

## 2. Nguyên tắc làm việc với dữ liệu nguồn

Hai file Excel là nguồn dữ liệu chỉ đọc:

- `export.xlsx`: dữ liệu được xuất từ SAP.
- `work flow locate 28072026.xlsx`: tài liệu mô tả cấu trúc dữ liệu và điều kiện xuất báo cáo SAP.
- Không chỉnh sửa nội dung, cấu trúc, tên cột, thứ tự cột hoặc giá trị trong hai file.
- Không tự sửa các giá trị trống, `0`, `N/A`, ngày tháng hoặc dữ liệu bất thường.
- Mỗi lần import phải lưu tên file, file hash và số dòng Excel để có thể truy vết.
- Thư mục chứa file nguồn được mount vào Import Worker ở chế độ read-only.

Ví dụ nguyên tắc mount:

```yaml
volumes:
  - ./data/source:/app/data/source:ro
```

Nếu người dùng upload file từ Web App, file upload được lưu vào một volume riêng. Thao tác này không ghi đè hoặc thay đổi hai file nguồn gốc.

## 3. Kiến trúc container

### 3.1. Service web

Nhiệm vụ:

- Cung cấp giao diện cho người dùng.
- Hiển thị danh sách và chi tiết dữ liệu SAP.
- Cho phép tìm kiếm, lọc và phân trang.
- Cung cấp màn hình upload/import Excel.
- Hiển thị lịch sử và kết quả import.

Web App chỉ giao tiếp với Web API, không kết nối trực tiếp SQL Server.

### 3.2. Service api

Nhiệm vụ:

- Cung cấp REST API.
- Xác thực và phân quyền người dùng.
- Đọc dữ liệu từ SQL Server.
- Nhận file upload và tạo yêu cầu import.
- Trả về trạng thái, log và lỗi import.
- Cung cấp interface để kết nối AI Service sau này.

### 3.3. Service import-worker

Nhiệm vụ:

- Đọc file Excel ở chế độ chỉ đọc.
- Kiểm tra cấu trúc 149 cột.
- Import dữ liệu vào Staging.
- So sánh Staging với bảng chính.
- Insert dữ liệu mới và update dữ liệu thay đổi.
- Ghi ImportBatch, ImportLog và ImportError.
- Không thay đổi file Excel nguồn.

Tách Import Worker khỏi Web API giúp các tác vụ import dài không làm chậm request của người dùng.

### 3.4. Service sqlserver

Nhiệm vụ:

- Lưu dữ liệu Staging và dữ liệu chính.
- Lưu lịch sử import và lỗi.
- Cung cấp dữ liệu cho Web API.
- Lưu dữ liệu trên named volume để database không mất khi container được tạo lại.

SQL Server không được public trực tiếp ra Internet.

### 3.5. Service db-migration

Đây là service chạy một lần khi triển khai:

- Tạo database schema.
- Tạo bảng, index và constraint.
- Áp dụng migration theo phiên bản.
- Kết thúc sau khi migration hoàn tất.

### 3.6. Service ai-service

Được bổ sung sau khi ETL và Web App ổn định:

- Nhận yêu cầu phân tích từ Web API.
- Đọc dữ liệu thông qua API hoặc SQL View chỉ đọc.
- Gọi AI Provider bằng API key lưu trên server.
- Trả về nội dung tóm tắt, phân tích hoặc đề xuất.
- Không được trực tiếp sửa dữ liệu nguồn hoặc dữ liệu SQL.

## 4. Cấu trúc thư mục dự án

```text
Project1/
├── src/
│   ├── Api/
│   ├── ImportWorker/
│   ├── WebApp/
│   ├── Application/
│   ├── Domain/
│   ├── Infrastructure/
│   └── AiService/              # bổ sung sau
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/
│   └── ImportTests/
├── database/
│   ├── migrations/
│   ├── scripts/
│   └── seed/
├── docker/
│   ├── api.Dockerfile
│   ├── worker.Dockerfile
│   ├── web.Dockerfile
│   └── ai.Dockerfile           # bổ sung sau
├── data/
│   ├── source/                 # Excel nguồn, mount read-only
│   ├── uploads/                # file upload
│   └── archive/                # file đã xử lý nếu nghiệp vụ cho phép
├── compose.yaml
├── compose.development.yaml
├── compose.production.yaml
├── .dockerignore
├── .env.example
└── README.md
```

Không commit các file sau vào Git:

- `.env` thật.
- Mật khẩu SQL Server.
- API key AI.
- Database backup.
- File Excel có dữ liệu nghiệp vụ nếu không được phép.

## 5. Docker Compose

`compose.yaml` định nghĩa cấu hình chung:

- `web`.
- `api`.
- `import-worker`.
- `sqlserver`.
- `db-migration`.
- Network nội bộ.
- Named volume cho SQL Server.
- Volume cho upload và log.
- Read-only bind mount cho Excel nguồn.
- Health check và dependency giữa các service.

`compose.development.yaml` dùng cho phát triển:

- Mở port cần debug.
- Mount source code nếu cần hot reload.
- Bật log chi tiết.
- Sử dụng cấu hình development.

`compose.production.yaml` dùng khi bàn giao:

- Không mount source code ứng dụng.
- Chỉ public cổng Web App hoặc reverse proxy.
- Không public cổng SQL Server.
- Đặt restart policy.
- Giới hạn log và tài nguyên.
- Dùng secret production.
- Bật HTTPS hoặc đặt phía sau reverse proxy HTTPS.

## 6. Thiết kế volume và dữ liệu bền vững

### 6.1. SQL Server volume

SQL Server phải sử dụng named volume:

```yaml
volumes:
  sql_data:
```

Mục tiêu:

- Database vẫn tồn tại khi container được tạo lại.
- Việc nâng cấp Web/API không ảnh hưởng database.
- Có thể backup và restore độc lập.

Không xem Docker volume là bản backup. Database vẫn phải được backup định kỳ ra vị trí ngoài container và ngoài volume chính.

### 6.2. Excel source mount

- Mount read-only vào Import Worker.
- Không mount vào Web App.
- Web API chỉ được truy cập file upload trong volume riêng.
- Không cấp quyền ghi lên thư mục nguồn.

### 6.3. Upload volume

- Web API ghi file upload vào volume `uploads`.
- Import Worker đọc file từ volume này.
- File được nhận diện bằng file hash.
- Không xử lý hai lần cùng một file nếu không có yêu cầu chạy lại.

### 6.4. Log và backup

- Log ứng dụng xuất ra standard output để xem bằng Docker logs.
- Log nghiệp vụ import được lưu trong SQL Server.
- Database backup được lưu ở thư mục hoặc storage ngoài container.
- Thiết lập chính sách giữ và xóa backup theo yêu cầu vận hành.

## 7. Thiết kế cơ sở dữ liệu

### 7.1. Bảng ImportBatch

Lưu thông tin mỗi lần import:

- ID lần import.
- Tên file và file hash.
- Nguồn file: upload hoặc thư mục cấu hình.
- Thời điểm bắt đầu và kết thúc.
- Trạng thái xử lý.
- Tổng số dòng đọc.
- Số dòng insert, update, unchanged và error.
- Thông báo lỗi tổng quát.

### 7.2. Bảng StagingSapData

- Giữ đầy đủ 149 trường theo file `export.xlsx`.
- Không làm sạch hoặc thay đổi giá trị nguồn.
- Có `ImportBatchId`.
- Có `SourceRowNumber`.
- Có thời điểm dữ liệu được đưa vào Staging.

### 7.3. Bảng SapData

- Có khóa chính nội bộ, ví dụ `Id`.
- Lưu dữ liệu phục vụ Web API và Web App.
- Có thông tin lần import đã tạo hoặc cập nhật bản ghi.
- Có thể lưu row hash để phát hiện dữ liệu thay đổi.

### 7.4. Bảng ImportLog

- `ImportBatchId`.
- Mức log: Information, Warning hoặc Error.
- Tên bước xử lý.
- Nội dung log.
- Thời điểm ghi log.

### 7.5. Bảng ImportError

- `ImportBatchId`.
- Số dòng Excel.
- Tên cột liên quan.
- Giá trị gốc nếu đọc được.
- Nội dung lỗi.
- Thời điểm phát sinh lỗi.

## 8. Quy tắc import

Import Worker thực hiện:

1. Nhận đường dẫn file hoặc yêu cầu import từ Web API.
2. Tính file hash.
3. Kiểm tra file đã được xử lý hay chưa.
4. Xác nhận đúng worksheet cần đọc.
5. Xác nhận đủ 149 cột.
6. Xác nhận tên và thứ tự cột khớp cấu trúc đã phân tích.
7. Nếu cấu trúc không khớp, dừng import và ghi log; không sửa file.
8. Tạo ImportBatch.
9. Đọc từng dòng và lưu số dòng Excel.
10. Ghi dữ liệu vào StagingSapData.
11. So sánh Staging với SapData.
12. Insert, update hoặc giữ nguyên bản ghi.
13. Ghi ImportLog và ImportError.
14. Hoàn thành ImportBatch.

Luồng tổng quát:

```text
Excel read-only
→ Validate structure
→ ImportBatch
→ StagingSapData
→ Compare
→ Insert / Update / Unchanged
→ ImportLog / ImportError
```

## 9. Cách so sánh dữ liệu

- Dùng khóa nghiệp vụ để tìm bản ghi tương ứng.
- Có thể kết hợp `Shipping Instructions ID` và `Unique Number` trong phép so sánh.
- Việc xử lý khóa trống chỉ nằm trong logic so sánh; không thay đổi giá trị lưu từ Excel.
- Tính hash từ các trường cần theo dõi để phát hiện thay đổi.
- Chưa có khóa tương ứng: Insert.
- Có khóa và hash thay đổi: Update.
- Có khóa và hash không thay đổi: Unchanged.
- Không xóa dữ liệu chính chỉ vì một bản ghi không xuất hiện trong file mới, trừ khi có quy tắc nghiệp vụ được xác nhận.

## 10. Web API

Các endpoint ban đầu:

```text
GET  /api/health
GET  /api/sap-data
GET  /api/sap-data/{id}
POST /api/imports
GET  /api/imports
GET  /api/imports/{id}
GET  /api/imports/{id}/logs
GET  /api/imports/{id}/errors
```

Yêu cầu:

- Phân trang phía server.
- Tìm kiếm và lọc dữ liệu.
- Sắp xếp theo cột.
- Giới hạn số bản ghi trên mỗi request.
- Không trả toàn bộ 9.726 dòng trong một request.
- Phân quyền người được xem dữ liệu và người được import.
- Có health check phục vụ Docker.
- Không trả connection string, secret hoặc đường dẫn nội bộ trong response.

## 11. Web App

### 11.1. Danh sách dữ liệu SAP

- Hiển thị dạng bảng.
- Phân trang.
- Tìm kiếm.
- Lọc và sắp xếp.
- Mở trang chi tiết bản ghi.

### 11.2. Chi tiết bản ghi

- Hiển thị đầy đủ thông tin.
- Nhóm trường theo nội dung nghiệp vụ.
- Hiển thị lần import gần nhất liên quan đến bản ghi.

### 11.3. Import Excel

- Chọn và upload file.
- Hiển thị tiến độ hoặc trạng thái xử lý.
- Hiển thị số dòng read, insert, update, unchanged và error.
- Không cung cấp chức năng sửa file nguồn.

### 11.4. Lịch sử import

- Danh sách các lần import.
- Trạng thái và thời gian xử lý.
- Tên file nguồn.
- Thống kê kết quả.
- Xem log và lỗi chi tiết.

## 12. Tự động hóa import

Thực hiện sau khi import thủ công ổn định:

- Theo dõi thư mục read-only hoặc thư mục nhận file riêng.
- Import theo lịch định kỳ.
- Cảnh báo khi file sai cấu trúc hoặc import thất bại.
- Tránh xử lý một file nhiều lần.
- Chỉ archive hoặc di chuyển bản sao làm việc khi nghiệp vụ cho phép.
- Không ghi đè hoặc sửa file nguồn.

## 13. Khả năng mở rộng AI

AI được triển khai thành container/service riêng:

```text
Web App
→ Web API
→ AI Service
→ AI Provider
```

Các chức năng có thể bổ sung:

- Tóm tắt trạng thái dữ liệu.
- Tìm kiếm bằng ngôn ngữ tự nhiên.
- Phân tích xu hướng.
- Phát hiện vấn đề cần chú ý.
- Đề xuất kế hoạch để người dùng xem xét.

Nguyên tắc AI:

- AI chỉ đọc dữ liệu cần thiết.
- AI không sửa Excel.
- AI không trực tiếp update hoặc delete dữ liệu SQL.
- Đề xuất của AI cần người dùng phê duyệt nếu liên quan đến hành động nghiệp vụ.
- API key chỉ lưu ở server thông qua secret hoặc environment variable.
- Ghi audit log cho yêu cầu AI khi đưa vào production.
- Không gửi dữ liệu nhạy cảm cho AI Provider nếu chưa được người phụ trách dữ liệu cho phép.

Không cần triển khai vector database ở giai đoạn đầu. Chỉ bổ sung khi use case AI thực tế yêu cầu tìm kiếm ngữ nghĩa hoặc RAG.

## 14. Bảo mật và phân quyền

- Chỉ public Web App/reverse proxy.
- API chỉ public khi thực sự cần và phải có xác thực.
- SQL Server chỉ nằm trong Docker network nội bộ.
- Không dùng tài khoản `sa` cho ứng dụng.
- Tạo tài khoản SQL có quyền tối thiểu cho API và Import Worker.
- Không ghi secret trong `compose.yaml` hoặc Git.
- Bật HTTPS trong môi trường production.
- Giới hạn loại và kích thước file upload.
- Kiểm tra phần mở rộng, nội dung và cấu trúc file trước khi import.
- Ghi audit log cho thao tác upload và import.

## 15. License và môi trường production

- Xác định edition/license SQL Server hợp lệ trước khi bàn giao production.
- Không dùng SQL Server Developer Edition cho production.
- Kiểm tra giới hạn SQL Server Express nếu chọn edition miễn phí.
- Kiểm tra điều khoản Docker Desktop nếu tổ chức dùng Docker Desktop cho mục đích thương mại.
- Với server production, ưu tiên Docker Engine và Docker Compose trên máy chủ Linux nếu hạ tầng cho phép.
- Pin phiên bản image; không phụ thuộc tùy ý vào tag `latest`.

## 16. Kiểm thử

### 16.1. Kiểm thử ETL

- Đọc đủ 9.726 dòng dữ liệu mẫu.
- Đọc đủ 149 cột.
- Dữ liệu SQL khớp dữ liệu Excel.
- Import lại cùng file không tạo dữ liệu trùng không mong muốn.
- File thay đổi chỉ update đúng bản ghi liên quan.
- File sai cấu trúc bị từ chối và có log rõ ràng.

### 16.2. Kiểm thử Docker

- Build thành công tất cả image.
- `docker compose up` khởi động toàn bộ hệ thống.
- Health check của SQL Server, API và Web App hoạt động.
- Service tự khởi động lại theo restart policy.
- Xóa và tạo lại container không làm mất database.
- Excel source mount thực sự là read-only.
- API không truy cập được secret không thuộc phạm vi của nó.

### 16.3. Kiểm thử backup/restore

- Tạo được backup SQL Server.
- Restore được database sang môi trường kiểm thử sạch.
- Xác nhận dữ liệu và lịch sử import sau restore.
- Ghi lại quy trình phục hồi trong README vận hành.

### 16.4. Kiểm thử người dùng

- Người dùng chỉ cần trình duyệt.
- Không yêu cầu cài Docker trên máy người dùng.
- Upload và theo dõi import thực hiện được từ giao diện.
- Thông báo lỗi dễ hiểu và không lộ thông tin kỹ thuật nhạy cảm.

## 17. Quy trình triển khai

Môi trường development:

```powershell
docker compose -f compose.yaml -f compose.development.yaml up --build -d
docker compose ps
docker compose logs -f
```

Môi trường production:

```powershell
docker compose -f compose.yaml -f compose.production.yaml pull
docker compose -f compose.yaml -f compose.production.yaml up -d
docker compose -f compose.yaml -f compose.production.yaml ps
```

Quy trình cập nhật:

1. Backup database.
2. Pull image phiên bản mới.
3. Chạy database migration.
4. Tạo lại container ứng dụng cần cập nhật.
5. Kiểm tra health check.
6. Kiểm tra import và truy vấn dữ liệu cơ bản.
7. Rollback image nếu kiểm tra thất bại.

## 18. Thứ tự triển khai đề xuất

1. Chốt công nghệ Web API và Web App.
2. Tạo cấu trúc source code Docker-first.
3. Tạo `compose.yaml` và SQL Server volume.
4. Tạo Dockerfile cho API, Import Worker và Web App.
5. Thiết kế database và migration.
6. Xây dựng module đọc Excel chỉ đọc.
7. Xây dựng import vào Staging.
8. Xây dựng logic Insert/Update/Unchanged.
9. Xây dựng ImportLog và ImportError.
10. Viết kiểm thử ETL và Docker.
11. Xây dựng Web API.
12. Xây dựng Web App.
13. Hoàn thiện authentication và authorization.
14. Thiết lập backup/restore.
15. Viết tài liệu cài đặt và vận hành.
16. Triển khai thử nghiệm cho người dùng.
17. Bổ sung import tự động.
18. Bổ sung AI Service khi có use case được duyệt.

## 19. Tiêu chí hoàn thành giai đoạn đầu

Giai đoạn đầu hoàn thành khi:

- Toàn bộ hệ thống chạy được bằng Docker Compose.
- Máy người dùng không cần cài Docker hoặc phần mềm phụ thuộc.
- Người dùng truy cập được bằng trình duyệt.
- File Excel được đọc mà không bị chỉnh sửa.
- Thư mục Excel nguồn được mount read-only.
- Dữ liệu được import đầy đủ vào SQL Server.
- Database tồn tại khi container được tạo lại.
- Import lại cùng file không tạo dữ liệu trùng không mong muốn.
- Dữ liệu thay đổi được phát hiện và cập nhật đúng.
- Mỗi lần import có lịch sử, log và lỗi rõ ràng.
- Có thể truy vết từ dữ liệu SQL về file và dòng Excel nguồn.
- Backup và restore SQL Server đã được kiểm thử.
- Có tài liệu hướng dẫn cài đặt, cập nhật, backup và xử lý sự cố.

## 20. Tài liệu tham khảo chính thức

- Docker Compose: <https://docs.docker.com/compose/>
- Docker Compose trong production: <https://docs.docker.com/compose/how-tos/production/>
- Docker bind mounts: <https://docs.docker.com/engine/storage/bind-mounts/>
- Docker volumes: <https://docs.docker.com/engine/storage/volumes/>
- SQL Server container configuration và persistence: <https://learn.microsoft.com/en-us/sql/linux/containers/configure>
- SQL Server container deployment và licensing: <https://learn.microsoft.com/en-us/sql/linux/containers/deploy>
- Docker Desktop licensing: <https://docs.docker.com/subscription/desktop-license/>

