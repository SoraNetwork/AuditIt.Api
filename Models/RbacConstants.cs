using System.Collections.Generic;

namespace AuditIt.Api.Models
{
    // 固定权限清单。部署后新增权限，只需往这里加一条并递增迁移即可。
    public static class PermissionCodes
    {
        public const string UserView = "user.view";
        public const string UserManage = "user.manage";
        public const string RoleManage = "role.manage";

        public const string WarehouseManage = "warehouse.manage";
        public const string CategoryManage = "category.manage";
        public const string ItemDefinitionManage = "itemdefinition.manage";

        public const string ItemView = "item.view";
        public const string ItemCreate = "item.create";
        public const string ItemUpdate = "item.update";
        public const string ItemDelete = "item.delete";
        public const string ItemTransfer = "item.transfer";

        public const string ListingManage = "listing.manage";

        public const string RenterView = "renter.view";
        public const string RenterManage = "renter.manage";

        public const string RentalView = "rental.view";
        public const string RentalCreate = "rental.create";
        public const string RentalUpdate = "rental.update";
        public const string RentalShip = "rental.ship";
        public const string RentalReturn = "rental.return";
        public const string RentalCancel = "rental.cancel";

        public const string ReminderView = "reminder.view";
        public const string ReminderCreate = "reminder.create";
        public const string ReminderDismissAny = "reminder.dismiss.any";

        public const string FinanceReportView = "finance.report.view";

        public const string AuditLogView = "auditlog.view";

        public static readonly IReadOnlyList<(string Code, string Category, string Description)> Catalog = new List<(string, string, string)>
        {
            (UserView,             "用户",   "查看用户列表"),
            (UserManage,           "用户",   "禁用/启用员工、修改档案"),
            (RoleManage,           "用户",   "增删角色及其权限绑定"),
            (WarehouseManage,      "基础档案", "仓库增删改"),
            (CategoryManage,       "基础档案", "分类增删改"),
            (ItemDefinitionManage, "基础档案", "物品模板增删改"),
            (ItemView,             "物品",   "查看物品"),
            (ItemCreate,           "物品",   "入库/新建物品"),
            (ItemUpdate,           "物品",   "编辑物品"),
            (ItemDelete,           "物品",   "处置/删除物品"),
            (ItemTransfer,         "物品",   "库房调拨"),
            (ListingManage,        "物品",   "挂载平台链接增删改"),
            (RenterView,           "租客",   "查看租客"),
            (RenterManage,         "租客",   "新建/修改/删除租客"),
            (RentalView,           "租赁",   "查看租赁单"),
            (RentalCreate,         "租赁",   "新建租赁单"),
            (RentalUpdate,         "租赁",   "修改/延期租赁单"),
            (RentalShip,           "租赁",   "登记发货与签收"),
            (RentalReturn,         "租赁",   "归还"),
            (RentalCancel,         "租赁",   "取消"),
            (ReminderView,         "提醒",   "查看提醒"),
            (ReminderCreate,       "提醒",   "手动创建提醒"),
            (ReminderDismissAny,   "提醒",   "代他人忽略提醒"),
            (FinanceReportView,    "财务",   "查看租赁财务报表"),
            (AuditLogView,         "审计",   "查看审计日志"),
        };
    }

    public static class BuiltInRoles
    {
        public const string Admin = "Admin";
        public const string Manager = "Manager";
        public const string Operator = "Operator";
        public const string Viewer = "Viewer";

        public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            [Admin] = "系统管理员，拥有全部权限",
            [Manager] = "业务经理，可执行全部业务 + 查看审计/提醒",
            [Operator] = "日常作业员工",
            [Viewer] = "只读账号"
        };

        public static IReadOnlyList<string> PermissionsFor(string role) => role switch
        {
            Admin => PermissionCodes.Catalog.Select(p => p.Code).ToList(),
            Manager => new List<string>
            {
                PermissionCodes.UserView,
                PermissionCodes.WarehouseManage,
                PermissionCodes.CategoryManage,
                PermissionCodes.ItemDefinitionManage,
                PermissionCodes.ItemView, PermissionCodes.ItemCreate, PermissionCodes.ItemUpdate,
                PermissionCodes.ItemDelete, PermissionCodes.ItemTransfer,
                PermissionCodes.ListingManage,
                PermissionCodes.RenterView, PermissionCodes.RenterManage,
                PermissionCodes.RentalView, PermissionCodes.RentalCreate, PermissionCodes.RentalUpdate,
                PermissionCodes.RentalShip, PermissionCodes.RentalReturn, PermissionCodes.RentalCancel,
                PermissionCodes.ReminderView, PermissionCodes.ReminderCreate, PermissionCodes.ReminderDismissAny,
                PermissionCodes.AuditLogView,
            },
            Operator => new List<string>
            {
                PermissionCodes.ItemView, PermissionCodes.ItemCreate, PermissionCodes.ItemUpdate,
                PermissionCodes.ItemTransfer,
                PermissionCodes.ListingManage,
                PermissionCodes.RenterView, PermissionCodes.RenterManage,
                PermissionCodes.RentalView, PermissionCodes.RentalCreate, PermissionCodes.RentalUpdate,
                PermissionCodes.RentalShip, PermissionCodes.RentalReturn,
                PermissionCodes.ReminderView,
            },
            Viewer => new List<string>
            {
                PermissionCodes.ItemView,
                PermissionCodes.RenterView,
                PermissionCodes.RentalView,
                PermissionCodes.ReminderView,
                PermissionCodes.AuditLogView,
            },
            _ => new List<string>()
        };
    }

    public static class PermissionClaimType
    {
        public const string Permission = "permission";
    }
}
