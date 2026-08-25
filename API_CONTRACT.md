# AuditIt.Api Current Effective Contract

Last updated: 2026-04-18

This document is the current API contract baseline for `AuditIt.Ant` integration.

## Common

- Base URL: `/api`
- Auth: `Authorization: Bearer <token>` for all endpoints except auth login.
- Enum values are serialized as strings.
- Error body is not fully unified (some endpoints return string, some `{ error: string }`, some model state dictionary).

---

## Auth

### `POST /auth/dingtalk-login`
- Body: `{ code: string }`
- Response: `{ token: string, user: { id, name, status, lastLoginAt }, permissions: string[] }`

### `POST /auth/dingtalk-sso-login`
- Body: `{ code: string }`
- Response: same as above.

---

## Items

### `GET /items`
- Query:
  - `warehouseId?: number`
  - `status?: ItemStatus`
  - `id?: guid`
  - `shortId?: string`
  - `serialNumber?: string`
  - `search?: string`
- Response: `ItemDto[]`

`ItemDto` shape:
- `id: string`
- `shortId: string`
- `serialNumber?: string`
- `itemDefinitionId: number`
- `itemDefinitionName: string`
- `warehouseId: number`
- `warehouseName: string`
- `status: ItemStatus`
- `currentDestination?: string`
- `remarks?: string`
- `photoUrl?: string`
- `entryDate: string (ISO)`
- `lastUpdated: string (ISO)`

### `POST /items/batch`
- Body: `guid[]`
- Response: `ItemDto[]`

### `POST /items/create`
- Content-Type: `multipart/form-data`
- Body fields:
  - `itemDefinitionId: number`
  - `warehouseId: number`
  - `shortId?: string`
  - `serialNumber?: string`
  - `remarks?: string`
  - `photo?: file`
- Response: `ItemDto`

### `POST /items/create/batch`
- Body:
  - `itemDefinitionId: number`
  - `warehouseId: number`
  - `items: Array<{ shortId?: string; serialNumber?: string; remarks?: string }>`
- Response: `{ message: string }`

### `PUT /items/{id}`
- Content-Type: `multipart/form-data`
- Body fields:
  - `shortId?: string`
  - `serialNumber?: string`
  - `remarks?: string`
  - `currentDestination?: string`
  - `photo?: file`
  - `deletePhoto?: boolean`
- Response: `204 No Content`

### `PUT /items/{id}/outbound`
- Body: `{ destination?: string }`
- Response: `ItemDto`

### `PUT /items/{id}/check`
- Body: empty
- Response: `ItemDto`

### `PUT /items/{id}/return`
- Body: empty
- Response: `ItemDto`

### `PUT /items/{id}/dispose`
- Body: `{ destination?: string }`
- Response: `ItemDto`

### `POST /items/update-status/batch`
- Body: `{ itemIds: guid[]; status: ItemStatus }`
- Response: `{ message: string }`

### `PUT /items/{id}/transfer`
- Body: `{ newWarehouseId: number; remarks?: string }`
- Response: `ItemDto`

---

## Item Listings

### `GET /items/{itemId}/listings`
- Response: `ItemListingDto[]`

### `POST /items/{itemId}/listings`
- Body: `CreateItemListingDto`
- Response: `ItemListingDto`

### `PUT /listings/{id}`
- Body: `UpdateItemListingDto`
- Response: `ItemListingDto`

### `DELETE /listings/{id}`
- Response: `204 No Content`

---

## Master Data

### Categories
- `GET /categories`
- `POST /categories`
- `PUT /categories/{id}`
- `DELETE /categories/{id}`

### ItemDefinitions
- `GET /itemDefinitions`
- `POST /itemDefinitions`
- `PUT /itemDefinitions/{id}`
- `DELETE /itemDefinitions/{id}`

### Warehouses
- `GET /warehouses`
- `POST /warehouses`
- `PUT /warehouses/{id}`
- `DELETE /warehouses/{id}`

---

## Renters

### `GET /renters`
- Query: `keyword?: string`, `limit?: number`
- Response: `RenterDto[]`

### `GET /renters/{id}`
- Response: `RenterDto`

### `POST /renters`
- Body: `CreateRenterDto`
- Response: `RenterDto`

### `PUT /renters/{id}`
- Body: `UpdateRenterDto`
- Response: `RenterDto`

### `DELETE /renters/{id}`
- Response: `204 No Content`

---

## Rentals

### `GET /rentals`
- Query:
  - `status?: RentalStatus`
  - `renterId?: guid`
  - `rentalNumber?: string`
  - `search?: string`
  - `startDateFrom?: datetime`
  - `startDateTo?: datetime`
  - `pendingSettlement?: boolean`
  - `sortField?: expectedShipDate | startDate | expectedEndDate | expectedReturnDate`
  - `sortOrder?: ascend | descend`
  - `page?: number`
  - `pageSize?: number`
- Response: `{ items: RentalDto[]; total: number }`

### `GET /rentals/{id}`
- Response: `RentalDto`

### `POST /rentals`
- Body: `CreateRentalDto`
- `itemPrices` is optional for compatibility. When supplied, provide one `{ itemId | itemDefinitionId, perItemPrice }` for every selected item; the server calculates `totalPrice` from the entries.
- Response: `RentalDto`

### `PUT /rentals/{id}`
- Body: `UpdateRentalDto`
- Response: `RentalDto`

### `POST /rentals/{id}/ship`
- Body: `CreateShipmentDto`
- New clients explicitly send `rentalItemIds` for the outbound rental items selected for this parcel. An empty explicit list is rejected; omitting the property preserves legacy whole-rental behavior.
- `itemSelections` contains `{ rentalItemId, itemId }` mappings. Outbound mappings identify concrete inventory for uncertain rental items; inbound mappings associate return logistics with selected items.
- Response: `RentalDto`

### `GET /rentals/{id}/settlement`
- Response: `SettlementPreviewDto`

### `POST /rentals/{id}/settlement/send`
- Sends or re-sends one settlement message.
- Response: `SettlementPreviewDto`

### `POST /rentals/settlements/send`
- Body: `{ rentalIds: guid[] }` (1-100 IDs)
- Sends or re-sends each selected settlement and returns per-rental success/error details.
- Response: `BatchSendSettlementsResultDto`

### `POST /rentals/{id}/shipments/{shipmentId}/deliver`
- Body: `DeliverShipmentDto`
- Response: `RentalDto`

### `POST /rentals/{id}/return`
- Body: `ReturnRentalDto`
- When returning damaged items, set `repairOccupancy: true` and provide `repairExpectedReturnDate`. The returned physical items are then retained as a normal loan with destination/reason `损坏维修`.
- Response: `RentalDto`

### `POST /rentals/{id}/cancel`
- Body: `CancelRentalDto`
- Response: `RentalDto`

---

## Reminders

### `GET /reminders`
- Query:
  - `unreadOnly?: boolean`
  - `targetUser?: string`
  - `type?: ReminderType`
  - `limit?: number`
- Response: `ReminderDto[]`

### `POST /reminders/{id}/dismiss`
- Response: `204 No Content`

### `POST /reminders/dismiss-all`
- Query: `targetUser?: string`
- Response: `{ dismissed: number }`

### `POST /reminders`
- Body: `CreateReminderDto`
- Response: `ReminderDto[]`

---

## Shipment reminder settings

All endpoints require the `shipmentreminder.manage` permission. This permission is assigned to the built-in `Admin` role only.

### `GET /shipment-reminder-settings`
- Response: `ShipmentReminderSettingsDto`

### `GET /shipment-reminder-settings/recipients`
- Returns active employees as `{ id, name, mobile? }` for specified-administrator and test-recipient selectors. It is authorized by `shipmentreminder.manage`; it does not require the separate user-directory permission.

### `GET /shipment-reminder-settings/sms-templates`
- Loads SMS templates from the configured Alibaba Cloud account using `QuerySmsTemplateList`.
- Response: `AliyunSmsTemplateDto[]`; `matchesShipmentReminder` is true only for approved templates whose fixed text matches the shipment reminder text.

### `PUT /shipment-reminder-settings`
- Body: `UpdateShipmentReminderSettingsDto`
- `templateVariables` is a list of `{ name, source, staticValue? }` mappings. Sources support order/renter details, expected shipping dates, creator, responsible user, and static text.
- SMS templates are reloaded from Alibaba Cloud and the selected template's variable names must exactly match the submitted mappings.

### `POST /shipment-reminder-settings/test`
- Body: `{ userIds: guid[], sendSms: boolean, sendVoice: boolean }`
- Sends the configured template to selected active employees with valid mobile numbers, returning the provider request ID or error per channel.

### Server configuration

Store Alibaba Cloud credentials only in server-side configuration or a secret manager; do not put them in the browser build:

```json
"AliyunNotification": {
  "AccessKeyId": "<ram-access-key-id>",
  "AccessKeySecret": "<ram-access-key-secret>"
}
```

The RAM identity needs SMS template-list and SMS-send access plus DYVMS `SingleCallByTts` access. The scheduler uses China time. SMS and voice calls have independent send times (defaults: SMS 12:00, voice 12:30). Both channels target rentals whose expected shipping date is today or earlier and which have no outbound shipment. The relative date variable produces `今天`, `昨天`, or `N天前` for overdue orders.

---

## RBAC

### Roles / Permissions
- `GET /roles` -> `RoleDto[]`
- `GET /roles/{id}` -> `RoleDto`
- `POST /roles` -> `RoleDto`
- `PUT /roles/{id}` -> `RoleDto`
- `DELETE /roles/{id}` -> `204`
- `GET /permissions` -> `PermissionDto[]`

### Users
- `GET /users` (query: `keyword`, `status`, `role`, `limit`) -> `UserDto[]`
- `GET /users/{id}` -> `UserDto`
- `PUT /users/{id}/status` (body: `UpdateUserStatusDto`) -> `UserDto`
- `PUT /users/{id}/roles` (body: `AssignRolesDto`) -> `UserDto`

---

## Audit Logs

### `GET /auditLogs`
- Query: `itemId?: guid`
- Response: `AuditLog[]`
