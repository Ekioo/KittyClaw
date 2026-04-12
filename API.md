# Todo.Web | v1

Base URL: `http://localhost:5230/`

## Columns

### GET /api/projects/{slug}/columns

```
GET /api/projects/{slug}/columns
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Responses:**

- `200` OK

---

### POST /api/projects/{slug}/columns

```
POST /api/projects/{slug}/columns
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Request body:** `application/json`

```json
{
  "name": "...",
  "color": "..."  // optional
}
```

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/columns/{columnId}

```
DELETE /api/projects/{slug}/columns/{columnId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `columnId` | path | integer (int32) | Yes |
| `moveTicketsTo` | query | string | Yes |

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/columns/reorder

```
PATCH /api/projects/{slug}/columns/reorder
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Request body:** `application/json`

```json
{
  "columnId": 0,
  "index": 0
}
```

**Responses:**

- `200` OK

---

## Projects

### GET /api/projects

```
GET /api/projects
```

**Responses:**

- `200` OK

---

### POST /api/projects

```
POST /api/projects
```

**Request body:** `application/json`

```json
{
  "name": "..."
}
```

**Responses:**

- `200` OK

---

### GET /api/projects/{slug}

```
GET /api/projects/{slug}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}

```
DELETE /api/projects/{slug}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Responses:**

- `200` OK

---

## Tickets

### GET /api/projects/{slug}/tickets

```
GET /api/projects/{slug}/tickets
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `status` | query | string | No |
| `priority` | query | `TicketPriority` | No |
| `assignedTo` | query | string | No |
| `createdBy` | query | string | No |
| `search` | query | string | No |
| `parentId` | query | integer (int32) | No |

**Responses:**

- `200` OK

---

### POST /api/projects/{slug}/tickets

```
POST /api/projects/{slug}/tickets
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Request body:** `application/json`

```json
{
  "title": "...",
  "createdBy": "...",
  "status": "...",
  "description": "..."  // optional,
  "labelIds": []  // optional,
  "priority": "Idea"  // optional,
  "assignedTo": "..."  // optional,
  "parentId": 0  // optional
}
```

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/tickets/{id}

```
PATCH /api/projects/{slug}/tickets/{id}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "author": "...",
  "title": "..."  // optional,
  "description": "..."  // optional,
  "priority": null  // optional,
  "assignedTo": "..."  // optional
}
```

**Responses:**

- `200` OK

---

### GET /api/projects/{slug}/tickets/{id}

```
GET /api/projects/{slug}/tickets/{id}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/tickets/{id}

```
DELETE /api/projects/{slug}/tickets/{id}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/tickets/{id}/status

```
PATCH /api/projects/{slug}/tickets/{id}/status
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "status": "...",
  "author": "..."
}
```

**Responses:**

- `200` OK

---

### PUT /api/projects/{slug}/tickets/{id}/parent

```
PUT /api/projects/{slug}/tickets/{id}/parent
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "parentId": 0
}
```

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/tickets/{id}/parent

```
DELETE /api/projects/{slug}/tickets/{id}/parent
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/tickets/{id}/reorder

```
PATCH /api/projects/{slug}/tickets/{id}/reorder
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "status": "...",
  "index": 0
}
```

**Responses:**

- `200` OK

---

## Comments

### POST /api/projects/{slug}/tickets/{id}/comments

```
POST /api/projects/{slug}/tickets/{id}/comments
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "content": "...",
  "author": "..."
}
```

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}

```
PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |
| `commentId` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "content": "...",
  "author": "..."
}
```

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}

```
DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |
| `commentId` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

## Activity

### GET /api/projects/{slug}/tickets/{id}/activity

```
GET /api/projects/{slug}/tickets/{id}/activity
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

## Labels

### GET /api/projects/{slug}/labels

```
GET /api/projects/{slug}/labels
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Responses:**

- `200` OK

---

### POST /api/projects/{slug}/labels

```
POST /api/projects/{slug}/labels
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Request body:** `application/json`

```json
{
  "name": "...",
  "color": "..."  // optional
}
```

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/labels/{labelId}

```
DELETE /api/projects/{slug}/labels/{labelId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `labelId` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/labels/{labelId}

```
PATCH /api/projects/{slug}/labels/{labelId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `labelId` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "name": "..."  // optional,
  "color": "..."  // optional
}
```

**Responses:**

- `200` OK

---

### GET /api/projects/{slug}/tickets/{id}/labels

```
GET /api/projects/{slug}/tickets/{id}/labels
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

### PUT /api/projects/{slug}/tickets/{id}/labels

```
PUT /api/projects/{slug}/tickets/{id}/labels
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `id` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "labelIds": []
}
```

**Responses:**

- `200` OK

---

## Members

### GET /api/projects/{slug}/members

```
GET /api/projects/{slug}/members
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Responses:**

- `200` OK

---

### POST /api/projects/{slug}/members

```
POST /api/projects/{slug}/members
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |

**Request body:** `application/json`

```json
{
  "name": "..."
}
```

**Responses:**

- `200` OK

---

### PATCH /api/projects/{slug}/members/{memberId}

```
PATCH /api/projects/{slug}/members/{memberId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `memberId` | path | integer (int32) | Yes |

**Request body:** `application/json`

```json
{
  "name": "..."
}
```

**Responses:**

- `200` OK

---

### DELETE /api/projects/{slug}/members/{memberId}

```
DELETE /api/projects/{slug}/members/{memberId}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `memberId` | path | integer (int32) | Yes |

**Responses:**

- `200` OK

---

## Mentions

### GET /api/projects/{slug}/mentions/{handle}

```
GET /api/projects/{slug}/mentions/{handle}
```

**Parameters:**

| Name | In | Type | Required |
|------|-----|------|----------|
| `slug` | path | string | Yes |
| `handle` | path | string | Yes |
| `since` | query | string (date-time) | No |
| `until` | query | string (date-time) | No |

**Responses:**

- `200` OK

---

## Images

### POST /api/images

```
POST /api/images
```

**Responses:**

- `200` OK

---

## Models

### AddCommentRequest

| Field | Type | Required |
|-------|------|----------|
| `content` | string | Yes |
| `author` | string | Yes |

### CreateColumnRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string | Yes |
| `color` | string | No |

### CreateLabelRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string | Yes |
| `color` | string | No |

### CreateMemberRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string | Yes |

### CreateProjectRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string | Yes |

### CreateTicketRequest

| Field | Type | Required |
|-------|------|----------|
| `title` | string | Yes |
| `createdBy` | string | Yes |
| `status` | string | Yes |
| `description` | string | No |
| `labelIds` | integer (int32)[] | No |
| `priority` | `TicketPriority` | No |
| `assignedTo` | string? | No |
| `parentId` | integer (int32) | No |

### MoveTicketRequest

| Field | Type | Required |
|-------|------|----------|
| `status` | string | Yes |
| `author` | string | Yes |

### ReorderColumnRequest

| Field | Type | Required |
|-------|------|----------|
| `columnId` | integer (int32) | Yes |
| `index` | integer (int32) | Yes |

### ReorderTicketRequest

| Field | Type | Required |
|-------|------|----------|
| `status` | string | Yes |
| `index` | integer (int32) | Yes |

### SetParentRequest

| Field | Type | Required |
|-------|------|----------|
| `parentId` | integer (int32) | Yes |

### SetTicketLabelsRequest

| Field | Type | Required |
|-------|------|----------|
| `labelIds` | integer (int32)[] | Yes |

### TicketPriority

Enum values:

- `Idea`
- `NiceToHave`
- `Required`
- `Critical`
- `null`

### UpdateCommentRequest

| Field | Type | Required |
|-------|------|----------|
| `content` | string | Yes |
| `author` | string | Yes |

### UpdateLabelRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string? | No |
| `color` | string? | No |

### UpdateMemberRequest

| Field | Type | Required |
|-------|------|----------|
| `name` | string | Yes |

### UpdateTicketRequest

| Field | Type | Required |
|-------|------|----------|
| `author` | string | Yes |
| `title` | string? | No |
| `description` | string? | No |
| `priority` | object | No |
| `assignedTo` | string? | No |

---

## Conventions

- `createdBy` / `author`: `"owner"` pour l'utilisateur, `"agent:{name}"` pour les agents (ex: `"agent:claude"`)
- OpenAPI JSON: `GET /openapi/v1.json`
- Cette doc est auto-générée depuis la spec OpenAPI.

