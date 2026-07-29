# Tóm tắt yêu cầu dự án SAP → SQL → Web App

## Mục tiêu dự án

Xây dựng một hệ thống **tự động đồng bộ dữ liệu từ SAP sang SQL
Server**, sau đó cung cấp **Web App** để người dùng xem dữ liệu. Nếu có
thể thì tích hợp **AI Generative** để hỗ trợ phân tích hoặc lập kế
hoạch.

------------------------------------------------------------------------

## Quy trình làm việc

``` text
SAP
↓
Export dữ liệu ra Excel
↓
Script tự động đọc file Excel
↓
Import vào bảng Staging (SQL Server)
↓
So sánh với bảng chính
↓
Insert dữ liệu mới
Update dữ liệu thay đổi
↓
Lưu Log lịch sử xử lý
↓
Web App hiển thị dữ liệu
↓
(Tùy chọn) AI Generative hỗ trợ lập kế hoạch
```

------------------------------------------------------------------------

## Vai trò của hai file Excel

### 1. export.xlsx

-   Là dữ liệu được export từ SAP.
-   Đây là dữ liệu đầu vào để import vào SQL Server.

### 2. work flow locate.xlsx

-   Là tài liệu mô tả workflow và cấu trúc dữ liệu.
-   Giải thích ý nghĩa các cột, kiểu dữ liệu và nguồn dữ liệu từ SAP.

------------------------------------------------------------------------

## Các chức năng cần thực hiện

1.  Tự động đọc file Excel từ SAP.
2.  Import dữ liệu vào bảng **Staging**.
3.  So sánh Staging với bảng chính.
4.  Insert dữ liệu mới.
5.  Update dữ liệu đã thay đổi.
6.  Ghi log mỗi lần import.
7.  Xây dựng Web App để xem và tra cứu dữ liệu.
8.  (Nếu có) Tích hợp AI Generative để hỗ trợ tạo kế hoạch hoặc phân
    tích dữ liệu.

------------------------------------------------------------------------

## Dự án có phải làm Web không?

**Có**, nhưng Web chỉ là **phần giao diện** của hệ thống.

Phần quan trọng nhất vẫn là xử lý dữ liệu:

-   Excel → SQL Server
-   So sánh dữ liệu
-   Insert/Update
-   Ghi Log

Sau khi dữ liệu đã được xử lý, Web App sẽ dùng để hiển thị và tra cứu dữ
liệu cho người dùng.

------------------------------------------------------------------------

## Kiến trúc tổng quát

``` text
SAP
↓
Export Excel
↓
Script Import (C#/Python)
↓
SQL Server
↓
Web API
↓
Web App
↓
AI (tùy chọn)
```

------------------------------------------------------------------------

## Kết luận

Đây là một **dự án ETL kết hợp Web Application**:

-   **ETL** là phần cốt lõi (đọc Excel, xử lý dữ liệu, đồng bộ SQL).
-   **Web App** là giao diện để người dùng sử dụng.
-   **AI Generative** là tính năng mở rộng nhằm tăng giá trị của hệ
    thống, không phải yêu cầu bắt buộc.
