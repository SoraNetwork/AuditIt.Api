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
- Response: `RentalDto`

### `PUT /rentals/{id}`
- Body: `UpdateRentalDto`
- Response: `RentalDto`

### `POST /rentals/{id}/ship`
- Body: `CreateShipmentDto`
- Response: `RentalDto`

### `POST /rentals/{id}/shipments/{shipmentId}/deliver`
- Body: `DeliverShipmentDto`
- Response: `RentalDto`

### `POST /rentals/{id}/return`
- Body: `ReturnRentalDto`
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
