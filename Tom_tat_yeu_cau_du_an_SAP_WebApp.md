# Tóm tắt yêu cầu dự án SAP DataSync System

## 1. Mục tiêu

Xây dựng một hệ thống tự động lấy dữ liệu Excel được export từ SAP, đồng bộ vào SQL Server và cung cấp Web App để người dùng xem dữ liệu.

Nếu phần chính hoạt động ổn định, hệ thống có thể tích hợp Generative AI để tự động tạo kế hoạch dựa trên dữ liệu.

## 2. Quy trình chính

```text
SAP
↓
Export `export.xlsx` hoặc file mới `export_YYYYMMDD_HHmmss.xlsx`
↓
Python ETL Worker tạo snapshot SHA-256 trong `data/archive`
↓
Python ETL Worker tự động đồng bộ mỗi ngày một lần
↓
Import vào bảng Staging trên SQL Server
↓
So sánh Staging với bảng dữ liệu chính
↓
Insert dữ liệu mới
Update dữ liệu thay đổi
Soft delete dữ liệu không còn trong snapshot đầy đủ
↓
Lưu lịch sử xử lý và kết quả import
↓
Web App hiển thị dữ liệu cho người dùng
↓
Generative AI tự động tạo kế hoạch — nếu thực hiện được
```

## 3. Vai trò của hai file Excel

### `export.xlsx` hoặc `export_YYYYMMDD_HHmmss.xlsx`

- Là dữ liệu được export từ SAP.
- Là đầu vào của script tự động import.
- File được đọc ở chế độ chỉ đọc.
- Hệ thống không chỉnh sửa nội dung hoặc cấu trúc file.
- Mỗi nội dung mới được snapshot vào `data/archive` trước khi import; cùng SHA-256 không tạo snapshot trùng.
- File không được commit lên Git.

### `work flow locate 28072026.xlsx`

- Là tài liệu mô tả cấu trúc dữ liệu và quy trình export từ SAP.
- Dùng để xác định tên cột, thứ tự cột, kiểu dữ liệu và ghi chú nghiệp vụ.
- File chỉ dùng làm tài liệu tham chiếu và không được chỉnh sửa.
- File không được commit lên Git.

## 4. Chức năng bắt buộc

1. Python ETL Worker tự động quét `export*.xlsx` một lần mỗi ngày, hỗ trợ cả file cố định bị ghi đè và file mới có timestamp.
2. Snapshot file theo SHA-256 vào `data/archive` trước khi import.
3. Kiểm tra cấu trúc 149 cột.
4. Import dữ liệu vào bảng Staging.
5. So sánh dữ liệu Staging với bảng chính.
6. Insert bản ghi mới.
7. Update bản ghi đã thay đổi.
8. Soft delete bản ghi không còn trong snapshot đầy đủ của cùng Product/Sales Organization; không xóa vật lý.
9. Audit Insert/Update/Soft Delete theo SapData ID, khóa nghiệp vụ, dòng Excel và giá trị cũ → mới.
10. Không update lại bản ghi không thay đổi.
11. Lưu lịch sử của mỗi lần import.
12. Cung cấp Web App để xem và tra cứu dữ liệu.
13. Chạy toàn bộ hệ thống bằng Docker.
14. Cho phép upload `.xlsx` vào vùng lưu trữ bền vững `data/uploads`; ETL đọc-only và snapshot sang `data/archive` trước khi import.

Soft delete có chốt cấu hình `ETL_ENABLE_SOFT_DELETE`; mẫu triển khai mặc định tắt và chỉ bật sau khi xác nhận file là snapshot đầy đủ.

Upload hiện dành cho nội bộ/test. Trước khi public Internet phải bổ sung authentication/authorization. Restore từ archive là thao tác quản trị có Preview và audit, không phải rollback tự do.

## 5. Chức năng tùy chọn

- Tích hợp Generative AI.
- Cho phép người dùng chọn hoặc lọc dữ liệu cần phân tích.
- Tạo bản kế hoạch dựa trên dữ liệu được chọn.
- Hiển thị kết quả để người dùng xem xét.

AI chỉ tạo nội dung đề xuất, không tự động sửa Excel hoặc dữ liệu SQL.

## 6. Kiến trúc MVP

```text
Docker Compose
├── web-api
│   └── ASP.NET Core Web App và Web API
├── etl-worker
│   └── Python tự động đọc và đồng bộ Excel
├── ai-service — bổ sung sau
│   └── Python Generative AI tạo kế hoạch
└── sqlserver
    ├── Staging
    ├── Dữ liệu chính
    └── Lịch sử import
```

Người dùng cuối chỉ truy cập Web App bằng trình duyệt và không phải cài Docker.

Quyết định công nghệ:

- Web App và Web API tiếp tục sử dụng ASP.NET Core.
- Tác vụ ETL tự động sử dụng Python và chạy trong container riêng.
- ETL dùng lịch chạy một lần mỗi ngày, không chạy bên trong request của Web API.
- Generative AI được phát triển bằng Python trong container riêng khi phần bắt buộc đã ổn định.
- ASP.NET Core, ETL và AI trao đổi qua API hoặc SQL Server; không đặt chung trong một tiến trình.

Trạng thái hiện tại: Python ETL Worker đã thay thế container C# và vượt qua cùng bộ kiểm thử 9.726 dòng. Mã C# chỉ còn được giữ làm bản tham chiếu.

## 7. Phạm vi MVP

MVP được xem là hoàn thành khi:

- Docker Compose khởi động được toàn bộ hệ thống.
- Python ETL Worker đọc được file Excel mà không chỉnh sửa file.
- Dữ liệu được đưa vào Staging.
- Dữ liệu mới được insert vào bảng chính.
- Dữ liệu thay đổi được update.
- Dữ liệu bị thiếu được soft delete và có thể khôi phục cùng ID nếu xuất hiện lại.
- Insert/Update/Soft Delete có audit chi tiết.
- Mỗi lần import có lịch sử và kết quả rõ ràng.
- Người dùng xem và tìm kiếm được dữ liệu trên Web App.

Generative AI là phần cộng thêm và chỉ triển khai sau khi luồng import và Web App hoạt động ổn định.

## 8. Ngoài phạm vi MVP

Các nội dung sau không phải yêu cầu bắt buộc của phiên bản đầu:

- Kubernetes hoặc triển khai đa máy chủ.
- Microservice phức tạp.
- Message queue.
- Dashboard phân tích nâng cao.
- Phân quyền doanh nghiệp nhiều cấp.
- Đồng bộ dữ liệu ngược về SAP.
- AI tự động thực hiện hành động nghiệp vụ.
- CI/CD và monitoring nâng cao.
