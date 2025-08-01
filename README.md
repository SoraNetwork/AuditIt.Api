# SoraAuditIt - 后端 API (AuditIt.Api)

这是 SoraAuditIt 库存管理系统的后端 API，基于 ASP.NET Core 8 构建。它为管理仓库、分类、物品定义和物品提供了所有必要的接口。

## 功能特性

- **RESTful API**: 为所有应用数据提供了一套完整的 RESTful 接口。
- **Entity Framework Core**: 使用 EF Core 进行数据访问和数据库迁移。
- **SQLite**: 使用 SQLite 作为数据库，以实现简单性和可移植性。
- **身份验证**: 已准备好基于 JWT 的身份验证（相关接口已就位）。
- **CRUD 操作**: 完全支持对所有核心实体的创建、读取、更新和删除操作。

## 安装与运行

1.  **环境要求**
    - [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

2.  **运行应用**
    - 进入 `AuditIt.Api` 目录。
    - 运行以下命令启动后端服务器：
    ```bash
    dotnet run
    ```
    - 默认情况下，API 将在 `http://localhost:5000` 和 `https://localhost:5001` 上可用。

3.  **数据库**
    - 应用使用一个 SQLite 数据库文件 (`auditit.db`)，该文件会在项目根目录中自动创建。
    - 数据库迁移已创建好。应用启动时会自动创建或更新数据库。
