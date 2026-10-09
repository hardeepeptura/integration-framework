# Neuro-Eptura — Building Your First Workflow

A plain-English guide for anyone new to the tool. No programming background needed.
This same guide is available inside the app under **Help**.

---

## 1. What is this tool?

Neuro-Eptura connects two systems together so that information flows between them **automatically**, without anyone copying and pasting.

You describe the flow as a **workflow** — a small chain of steps, drawn as boxes on a canvas. Each box does one small job (call a web address, read a database, reshuffle the data, make a decision…). When the chain starts, each box runs in order and hands its result to the next one.

**Example:** *"Every morning, read yesterday's orders from our SQL database, tidy up the columns, and post them to our partner's web service."* — that's three boxes in a row.

Two ready-made examples come with the tool — open them in the builder to learn by example:
- **Sample: API to API — CRM leads to Inventory kits** (pull a list from one API, loop over it, push to another API)
- **Sample: OAuth2 API pull — secure orders to Inventory** (the same idea, but the source API needs a token login)

---

## 2. Before you start

1. Open the tool: **https://integration-test.stg.epturacloud.com**
2. Click **Sign in** and use your **company (corporate) account**. Your name should appear at the top-right — that's how you know you're signed in.
3. The menus across the top:
   - **Dashboard** – one-page summary: how many workflows, how many runs, pass/fail rates
   - **Projects** – folders that group related workflows together
   - **Workflows** – build and manage your flows (you'll spend most of your time here)
   - **Connections** – saved logins to the systems you want to talk to
   - **Runs** – the history of every time a workflow executed
   - **Webhooks** – incoming deliveries and a way to replay them
   - **Users** *(admins only)* – manage who is an admin
   - **Help** – this guide

**Who sees what:** the first person to sign in becomes an **Admin**; everyone after is a **Contributor**.
- A **Contributor** sees their own workflows plus any workflow shared with them.
- An **Admin** sees and can manage everything.

---

## 3. Six words you need to know

| Word | What it means here |
|---|---|
| **Project** | A folder for grouping related workflows — pure organization, it does not change who can see a workflow. |
| **Workflow** | Your whole chain of steps, saved under a name. |
| **Node** (a box) | One step in the chain. Each box has a *name*, a *type* and its own settings. |
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
   - **Project** – which folder it belongs to (or leave it as *No project*).
3. Click **Create and open builder**.

### Step 2 — Meet the builder screen

- **Left panel** – the box types you can add. Click one to drop it on the canvas.
- **Middle** – the canvas: your boxes, drawn top to bottom in the order they run. Click a box to select it.
- **Right panel** – the settings for the box you selected.
- **Top-right buttons**:
  - **Save** – stores your changes.
  - **Validate** – checks the whole chain for mistakes (missing settings, broken links) *without* running it.
  - **Test run** – saves, then queues the chain to run once. It takes a second or two (runs execute on background workers) — then each box is coloured green (worked) or red (failed).

> Get in the habit of clicking **Validate** often. It catches typos before they become failed runs.

### Step 3 — Name each step

Every box has a **Step name** (shown at the top of its settings panel, e.g. `http_request_1`). The name is how later steps point at this step's output — so make it meaningful: rename `transform_1` to `tidy-orders` while you're setting it up.

Renaming is safe: when you change a step's name, **every reference to it updates everywhere automatically** — in mappings, URLs, conditions, loop bodies and branch lists. Names may only contain letters, digits, `-` and `_`.

### Step 4 — Configure each box

Click a box and fill in its settings on the right. Here's what each type does:

#### 🟠 Trigger *(always the first box)*
Choose **how the chain starts**:
- **Manual** – only when a person clicks *Run now*.
- **Webhook** – when another system sends an HTTP message to the workflow's special web address (see section 7).
- **Schedule** – automatically every *N* seconds (e.g. `86400` = once a day).

#### 🔵 HTTP Request — *call a web service*
Talks to any web address (API), like a browser that never gets tired.
- **URL** – the web address, e.g. `https://api.partner.com/orders`.
- **Method** – `GET` = *read*, `POST` = *create*, `PUT`/`PATCH` = *update*, `DELETE` = *remove*.
- **HTTP connection** *(optional)* – pick a saved connection if the service needs a login or an API key (including OAuth2 logins).
- **Headers / Query parameters / Body** – extra JSON details for the call. The **Body** is what you're sending for POST/PUT.
- **Timeout** – how long to wait before giving up (30 seconds is fine).

#### 🟣 DB Query — *read or write a database*
- **Database connection** – pick one you created on the **Connections** page (required).
- **SQL** – the database question, e.g.
  `SELECT * FROM orders WHERE status = :status`
  Anything written as `:name` is a **parameter** — a blank you fill at runtime.
- **Mode** – `Query` for reading (SELECT), `Execute` for changing data (INSERT/UPDATE/DELETE).
- **Parameters** – map each `:name` to a value or a **"$.\" reference** (see section 5).

#### 🟢 Transform — *reshape the data*
A mapping of **new field name → what to put in it**. Use `$.` references to pull values from earlier steps:
```json
{
  "orderId": "$.steps.fetch-orders.body[0].id",
  "customer": "$.steps.fetch-orders.body[0].name"
}
```

#### 🔴 Condition — *make a decision*
- **Left operand** – the thing to check, usually a `$.` reference.
- **Operator** – `eq` (equals), `ne` (not equal), `gt`/`lt` (greater/less than), `contains`, `exists`.
- **Right operand** – the value to compare with.
- **On true / On false** – tick which boxes should run when the answer is yes (green) or no (red). Boxes you tick will show connected with green (true) or red (false) lines.

#### 🟡 Loop — *do something for every item*
- **Source array** – a `$.` reference pointing at a list, e.g. `$.steps.fetch-orders.body`.
- **Body** – tick the boxes to repeat **once per item**. Inside the loop, the current item is `$.steps.<loop-name>.value`.

#### ⚪ Delay — *wait a moment*
Pauses the chain for the given number of seconds. Useful when the other system needs time to catch up.

#### ⚙️ Every box (except Trigger) also has
- **Retry attempts** – how many times to try again if it fails.
- **On error** – *Stop the run* (default) or *Continue with the next box*.

### Step 5 — Save, validate, test
1. Click **Save**.
2. Click **Validate** — fix anything it reports (usually invalid JSON or a missing setting).
3. Click **Test run** — it queues the run; a moment later the canvas shows boxes turning green in order. A red box shows its error; click it and read the settings again.

### Step 6 — See the results
Open the **Runs** page: every execution is listed with its status, and the list refreshes itself while runs are in flight. Click one to see each step's input, output and timing — invaluable when something misbehaves.

---

## 5. The "$." trick — pointing at data

Workflows pass data along, and `$.` is how a later box refers to something an earlier box produced:

| Reference | Means |
|---|---|
| `$.input` | The data the workflow was started with (e.g. the webhook payload). |
| `$.steps.fetch-orders.body` | The response body of the step *named* `fetch-orders`. |
| `$.steps.fetch-orders.body[0].id` | The `id` of the first item in that response. |
| `$.steps.each-order.value.name` | The `name` of the item the loop is currently on. |

Each step's name is shown in the builder — and you can change it (renaming updates all references). You can see all available data after any run on the **Runs** page — copy the paths straight from there.

---

## 6. Projects — keeping things tidy

Projects are **folders for workflows**. Use one per team, system or topic ("Facilities", "HR syncs", "Sandbox").

- Create them on the **Projects** page; the list shows how many workflows each holds.
- On the **Workflows** page, workflows are grouped under their project with **expand/collapse** headers, and there's a **project filter** at the top.
- Move a workflow between projects with the small dropdown in its **Project** column (or pick the project when creating it).
- The **Dashboard** can be scoped to one project via its project dropdown.
- A project that still contains workflows cannot be deleted — move or delete the workflows first, so nothing is lost by accident.

Projects are purely organizational: they do **not** change who can see or edit a workflow — that's what sharing and roles do.

---

## 7. Webhooks — letting other systems start your workflow

If the Trigger is set to **Webhook**, the workflow gets its own web address. Open the **Webhooks** page to:
- see every delivery that arrived (success or not — nothing is ever lost),
- **Simulate** a delivery yourself to test the chain without touching the other system — the delivery is queued, runs within a second or two, and the result is shown,
- **Replay** a past delivery — handy after you've fixed a mistake in the workflow.

Delivery statuses: **received** (arrived, waiting/running), **succeeded**, **failed**, **rejected** (workflow disabled or not webhook-triggered).

---

## 8. Connections — saved logins

On the **Connections** page (create/edit opens a popup), create a connection once and reuse it everywhere:

- **HTTP connection** – for web services. Auth types: none, API key header, Bearer token, Basic, or **OAuth2** (client credentials or refresh token — for services that need a proper corporate login; the secret is provided to the platform as an environment variable, never typed into the form).
- **Database connection** – server, database, username, password. Passwords are stored as environment variables on the server — ask your admin if one is missing (e.g. `MSSQL_PASSWORD`).

Use **Test connection** to make sure it works before wiring it into a workflow.

---

## 9. Sharing — working with others

- Workflows you create are yours; other people can't see them until you share.
- **Share** button (on your workflows) → add a colleague by email with **Can view** or **Can edit**:
  - *Can view* – they can see it and open the builder read-only.
  - *Can edit* – they can also change it and run it.
- **Admins** see and can manage every workflow without being shared on it.
- The **Owner** column on the Workflows page shows whose workflow it is.

---

## 10. Dashboard — how are we doing?

The **Dashboard** is the at-a-glance page:

- **Workflows** – how many you can see (all of them for admins).
- **Total runs** and the **pass/fail rates** over the selected time range — from the last hour up to the last 6 months.
- The **bar chart** shows successful (green) and failed (red) runs per time slot.
- **Busiest workflows** – your top 5 by run count.
- Pick a **project** in the dropdown to scope the whole page to that project.

---

## 11. Worked example — CRM lead → partner API

1. **Connections**: create an HTTP connection `Partner API` and a DB connection `CRM DB`. Test both.
2. **+ New workflow**: name *"New lead to partner"*, trigger **Webhook**, project *Sandbox* (optional).
3. In the builder, rename the HTTP box to `send-lead`: `POST https://partner.example.com/leads`, body `{"name": "$.input.name"}`.
4. Add **DB Query** `log-lead`: SQL `INSERT INTO lead_log (name) VALUES (:name)`, mode **Execute**, parameters `{ "name": "$.input.name" }`.
5. **Validate** → **Test run** (a manual run feeds `{}` — that's fine for a first smoke test).
6. Open **Webhooks**, use **Simulate** with `{ "name": "Jane Doe" }` — it queues, runs, and shows the result.
7. Done — every webhook delivery now runs the chain automatically, and **Webhooks** keeps the receipts.

---

## 12. When things go wrong

| Symptom | Likely cause | What to do |
|---|---|---|
| **Validate** complains about JSON | A typo in a JSON field — missing quote, comma, bracket | Fix the red "Invalid JSON" message on that field |
| **Validate** complains about a step name | Names may only use letters, digits, `-` and `_` | Rename the step in its settings panel |
| Run fails at **DB Query** | Wrong connection, or the password env var is missing | Test the connection; ask your admin for the password variable |
| Run fails at **HTTP Request** with a timeout | The other system is down or the URL is wrong | Check the URL; raise the timeout |
| **Condition** always goes down the false path | The `$.` reference doesn't point where you think | Open the last **Run** and copy the exact path |
| Webhook delivery shows **rejected** | Workflow disabled, or its trigger isn't "webhook" | Enable the workflow / fix the trigger type |
| A run stays queued for a long time | Rare — the background workers may be busy or down | Wait a minute; if it persists, contact the platform admin |
| Page asks you to sign in again | Your session expired (60 minutes of inactivity) | Click **Sign in** again |

Still stuck? Note the time of the failed run and contact the platform admin — every detail of every run is stored and traceable.

---

*Last updated: October 2026 — applies to Neuro-Eptura v0.5.4. This guide lives in the repository at `docs/How-To-Build-Workflows.md` and is shown in-app under **Help**.*
