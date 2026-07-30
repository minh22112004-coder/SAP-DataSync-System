# Database

Các script được Web/API chạy tuần tự theo tên file mỗi khi khởi động:

- `scripts/001_initial_schema.sql`: schema khởi tạo của MVP.
- `scripts/002_stage2_import.sql`: thêm hash phục vụ đối chiếu Staging và cơ chế chống import trùng.
- `scripts/003_sync_stored_procedure.sql`: stored procedure dùng chung để Update, Insert và đếm Unchanged.

Script tạo:

- Database `SapDataSync`.
- Bảng `ImportLog`.
- Bảng `SapDataStaging`.
- Bảng `SapData`.
- Index phục vụ tra cứu lịch sử import và đồng bộ dữ liệu.

Hai bảng dữ liệu chứa đủ 149 tên cột nguồn. Các giá trị nguồn ban đầu được lưu dưới dạng `NVARCHAR(MAX)` để Giai đoạn 2 có thể import nguyên trạng mà không áp đặt quy tắc làm sạch hoặc chuyển đổi dữ liệu.

Web/API tự chạy các script khi khởi động. Các script có thể chạy lại an toàn vì chỉ thêm database object còn thiếu.
