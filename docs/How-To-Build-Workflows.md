# Neuro-Eptura — Building Your First Workflow

A plain-English guide for anyone new to the tool. No programming background needed.

---

## 1. What is this tool?

Neuro-Eptura connects two systems together so that information flows between them **automatically**, without anyone copying and pasting.

You describe the flow as a **workflow** — a small chain of steps, drawn as boxes on a canvas. Each box does one small job (call a web address, read a database, reshuffle the data, make a decision…). When the chain starts, each box runs in order and hands its result to the next one.

**Example:** *"Every morning, read yesterday's orders from our SQL database, tidy up the columns, and post them to our partner's web service."* — that's three boxes in a row.

---

## 2. Before you start

1. Open the tool: **https://integration-test.stg.epturacloud.com**
2. Click **Sign in** and use your **company (corporate) account**. Your name should appear at the top-right — that's how you know you're signed in.
3. You will see four menus across the top:
   - **Workflows** – build and manage your flows (you'll spend most of your time here)
   - **Connections** – saved logins to the systems you want to talk to
   - **Runs** – the history of every time a workflow executed
   - **Webhooks** – incoming deliveries and a way to replay them

---

## 3. Five words you need to know

| Word | What it means here |
|---|---|
| **Workflow** | Your whole chain of steps, saved under a name. |
| **Node** (a box) | One step in the chain. Each box has a *type* and its own settings. |
| **Trigger** | The first box. It decides **when** the chain runs: by hand, by a web call from another system, or on a timer. |
| **Connection** | A saved login to a system (a database or a web service). You create it once on the **Connections** page, then reuse it in any workflow. |
| **Run** | One execution of the chain. Every run is recorded on the **Runs** page, step by step, success or failure. |

---

## 4. Step-by-step: build a workflow

### Step 1 — Create it
1. Go to **Workflows** and click **+ New workflow**.
2. Fill in:
   - **Name** – something you'll recognise later, e.g. *"Daily orders to partner"*.
   - **Description** – one line about what it does (optional but future-you will thank you).
   - **Trigger type** – how the chain starts. Not sure? Pick **Manual** — you can change it later.
3. Click **Create and open builder**.

### Step 2 — Meet the builder screen
- **Left panel** – the box types you can add. Click one to drop it on the canvas.
- **Middle** – the canvas: your boxes, drawn top to bottom in the order they run. Click a box to select it.
- **Right panel** – the settings for the box you selected.
- **Top-right buttons**:
  - **Save** – stores your changes.
  - **Validate** – checks the whole chain for mistakes (missing settings, broken links) *without* running it.
  - **Test run** – saves, then runs the chain once immediately, and colours each box green (worked) or red (failed).

> Get in the habit of clicking **Validate** often. It catches typos before they become failed runs.

### Step 3 — Configure each box

Click a box and fill in its settings on the right. Here's what each type does:

#### 🟠 Trigger *(always the first box)*
Choose **how the chain starts**:
- **Manual** – only when a person clicks *Run now*.
- **Webhook** – when another system sends an HTTP message to the workflow's special web address (see section 6).
- **Schedule** – automatically every *N* seconds (e.g. `86400` = once a day).

#### 🔵 HTTP Request — *call a web service*
Talks to any web address (API), like a browser that never gets tired.
- **URL** – the web address, e.g. `https://api.partner.com/orders`.
- **Method** – `GET` = *read*, `POST` = *create*, `PUT`/`PATCH` = *update*, `DELETE` = *remove*.
- **HTTP connection** *(optional)* – pick a saved connection if the service needs a login or an API key.
- **Headers / Query parameters / Body** – extra JSON details for the call. The **Body** is what you're sending for POST/PUT.
- **Timeout** – how long to wait before giving up (30 seconds is fine).

#### 🟣 DB Query — *read or write a database*
- **Database connection** – pick one you created on the **Connections** page (required).
- **SQL** – the database question, e.g.
  `SELECT * FROM orders WHERE status = :status`
  Anything written as `:name` is a **parameter** — a blank you fill at runtime.
- **Mode** – `Query` for reading (SELECT), `Execute` for changing data (INSERT/UPDATE/DELETE).
- **Parameters** – map each `:name` to a value or a **"$." reference** (see section 5).

#### 🟢 Transform — *reshape the data*
A mapping of **new field name → what to put in it**. Use `$.` references to pull values from earlier steps:
```json
{
  "orderId": "$.steps.db_query_1.rows[0].id",
  "customer": "$.steps.http_request_1.body.name"
}
```

#### 🔴 Condition — *make a decision*
- **Left operand** – the thing to check, usually a `$.` reference.
- **Operator** – `eq` (equals), `ne` (not equal), `gt`/`lt` (greater/less than), `contains`, `exists`.
- **Right operand** – the value to compare with.
- **On true / On false** – tick which boxes should run when the answer is yes (green) or no (red). Boxes you tick will show connected with green (true) or red (false) lines.

#### 🟡 Loop — *do something for every item*
- **Source array** – a `$.` reference pointing at a list, e.g. `$.steps.http_request_1.body.items`.
- **Body** – tick the boxes to repeat **once per item**.

#### ⚪ Delay — *wait a moment*
Pauses the chain for the given number of seconds. Useful when the other system needs time to catch up.

#### ⚙️ Every box (except Trigger) also has
- **Retry attempts** – how many times to try again if it fails.
- **On error** – *Stop the run* (default) or *Continue with the next box*.

### Step 4 — Save, validate, test
1. Click **Save**.
2. Click **Validate** — fix anything it reports (usually invalid JSON or a missing setting).
3. Click **Test run** — watch the canvas: boxes turn green in order. A red box shows its error; click it and read the settings again.

### Step 5 — See the results
Open the **Runs** page: every execution is listed with its status. Click one to see each step's input, output and timing — invaluable when something misbehaves.

---

## 5. The "$." trick — pointing at data

Workflows pass data along, and `$.` is how a later box refers to something an earlier box produced:

| Reference | Means |
|---|---|
| `$.input` | The data the workflow was started with (e.g. the webhook payload). |
| `$.steps.http_request_1.body` | The response body of the box with id `http_request_1`. |
| `$.steps.db_query_1.rows[0].id` | The `id` of the first row returned by that database box. |

Each box's id is shown in the builder (e.g. `db_query 1`). You can see all available data after any run on the **Runs** page — copy the paths straight from there.

---

## 6. Webhooks — letting other systems start your workflow

If the Trigger is set to **Webhook**, the workflow gets its own web address. Open the **Webhooks** page to:
- see every delivery that arrived (success or not — nothing is ever lost),
- **Simulate** a delivery yourself to test the chain without touching the other system,
- **Replay** a past delivery — handy after you've fixed a mistake in the workflow.

---

## 7. Connections — saved logins

On the **Connections** page, create a connection once and reuse it everywhere:

- **HTTP connection** – for web services. Supports plain headers (API keys) or **OAuth2** (client credentials / refresh token) for services that need a proper corporate login.
- **Database connection** – server, database, username, password. Passwords are stored as environment variables on the server — ask your admin if one is missing (e.g. `MSSQL_PASSWORD`).

Use **Test connection** to make sure it works before wiring it into a workflow.

---

## 8. Worked example — CRM lead → partner API

1. **Connections**: create an HTTP connection `Partner API` and a DB connection `CRM DB`. Test both.
2. **+ New workflow**: name *"New lead to partner"*, trigger **Webhook**.
3. In the builder, add **HTTP Request** `http_request_1`: `POST https://partner.example.com/leads`, body `{"name": "$.input.name"}`.
4. Add **DB Query** `db_query_1`: SQL `INSERT INTO lead_log (name) VALUES (:name)`, mode **Execute**, parameters `{ "name": "$.input.name" }`.
5. **Validate** → **Test run** (a manual run feeds `{}` — that's fine for a first smoke test).
6. Open **Webhooks**, use **Simulate** with `{ "name": "Jane Doe" }` and watch it go green.
7. Done — every webhook delivery now runs the chain automatically, and **Webhooks** keeps the receipts.

---

## 9. When things go wrong

| Symptom | Likely cause | What to do |
|---|---|---|
| **Validate** complains about JSON | A typo in a JSON field — missing quote, comma, bracket | Fix the red "Invalid JSON" message on that field |
| Run fails at **DB Query** | Wrong connection, or the password env var is missing | Test the connection; ask your admin for the password variable |
| Run fails at **HTTP Request** with a timeout | The other system is down or the URL is wrong | Check the URL; raise the timeout |
| **Condition** always goes down the false path | The `$.` reference doesn't point where you think | Open the last **Run** and copy the exact path |
| Webhook delivery shows **rejected** | Workflow disabled, or its trigger isn't "webhook" | Enable the workflow / fix the trigger type |
| Page asks you to sign in again | Your session expired (60 minutes of inactivity) | Click **Sign in** again |

Still stuck? Note the time of the failed run and contact the platform admin — every detail of every run is stored and traceable.

---

*Last updated: October 2026 — applies to Neuro-Eptura v0.3.6.*
