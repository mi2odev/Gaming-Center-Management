# mi2o Gaming Center — Management & POS

A Windows desktop app for running a gaming café: PS4, PS5, PCs, Xbox, Switch, simulators, or any other station an admin adds. Timed sessions, product sales, bills, payments, history, reports and backups all run on one laptop, with no internet or server.

**Stack:** C# · .NET 10 · WPF · MVVM (CommunityToolkit.Mvvm) · Entity Framework Core 10 · SQLite · Microsoft.Extensions.Hosting (DI, configuration, logging)

The UI follows the Claude Design handoff in [`design/`](design/): a dark theme, mint as the single action color, Sora for UI text and JetBrains Mono for timers and money. The dashboard is design **1a** (card grid) with **1c** available as a List view. At 1366×768 the sidebar collapses to icons (**1d**). The other screens follow 1e–1m.

---

## Quick start (Windows)

1. Install the **.NET 10 SDK** (https://dotnet.microsoft.com/download) and optionally Visual Studio 2026 / 2022 17.14+ with the ".NET desktop development" workload.
2. Build and run:
   ```powershell
   git clone <this repo>
   cd <repo>
   dotnet run --project src\GamingCenter.App
   ```
   Or open `GamingCenter.sln` in Visual Studio, set **GamingCenter.App** as the startup project and press F5.
3. Sign in with a demo account (created on first run):

   | Username | Password | Role |
   |---|---|---|
   | `admin` | `admin123` | Admin |
   | `operator` | `operator123` | Operator |

   **Change both passwords** from the sidebar user menu (bottom left → *Change password*), or from **Users**.

The first run creates the database and seeds demo stations (PS5, PS4, PC, Xbox, Switch, simulator) and products (drinks, snacks, food). They are ordinary data, so edit or delete them freely. To start empty, set `"SeedDemoData": false` in `appsettings.json` before the first run. The admin account is always created.

## Where data lives

Everything is local, in `%LOCALAPPDATA%\mi2oGamingCenter\` by default:

| Path | Content |
|---|---|
| `gamingcenter.db` | SQLite database (WAL mode) |
| `images\stations`, `images\products`, `images\branding` | Uploaded images, resized to ≤ 800 px; the database stores only the relative path |
| `backups\` | Automatic and manual backups (`gamingcenter-yyyyMMdd-HHmmss.db`) |
| `backups\before-restore\` | Safety copy taken automatically before any restore |
| `logs\app-yyyyMMdd.log` | Application log |

To use another folder (e.g. `D:\mi2o`), set it in `appsettings.json` next to the exe:

```json
{
  "Storage":  { "DataFolder": "D:\\mi2o" },
  "Database": { "SeedDemoData": true }
}
```

## Database & migrations

- The schema is managed with **EF Core migrations** in `src/GamingCenter.Infrastructure/Migrations`.
- On startup the app runs `Database.MigrateAsync()`, so pending migrations apply automatically and an existing database updates in place. There is no manual update step on the café laptop.
- To change the model (add a column, a new entity…):
  ```powershell
  # after editing entities / GamingCenterDbContext
  powershell -ExecutionPolicy Bypass -File scripts\add-migration.ps1 AddLoyaltyPoints
  # equivalent to:
  dotnet tool restore
  dotnet ef migrations add AddLoyaltyPoints --project src\GamingCenter.Infrastructure --startup-project src\GamingCenter.Infrastructure --output-dir Migrations
  ```
- To produce a SQL script for review: `dotnet ef migrations script --project src\GamingCenter.Infrastructure --startup-project src\GamingCenter.Infrastructure`.

## Tests

```powershell
dotnet test
```

The tests run on a real in-memory SQLite database, so transactions and constraints are exercised. They cover the billing maths (exact, per-minute, 15-minute units, minimum charge, rounding), pauses, locked prices, fixed-duration and fixed-budget rules, budget top-ups after auto-stop, stock movements, cancellation, counter sales, receipts, reports and role checks.

## Build a Windows executable (self-contained)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # folder build, win-x64
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SingleFile  # one mi2oGamingCenter.exe
```

The script runs the tests, then publishes to `publish\win-x64\`. Copy that folder to the café laptop and run `mi2oGamingCenter.exe`. The laptop does **not** need .NET installed. Equivalent command:

```powershell
dotnet publish src\GamingCenter.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o publish\win-x64
```

Only one copy of the app runs at a time on a machine, so timers and alerts never run twice on the same database.

---

## Using the app

### Operator flow (few clicks)
1. **Dashboard** shows every station as a card: image, name, type, room, status color, live timer, current cost, rate, customer and mode.
   🟢 Available · 🔴 Occupied · 🔵 Paused · 🟡 Reserved · ⚫ Offline/disabled · red "Time up" when a fixed session runs out.
2. Click an available card → **Start session** → choose *Play freely*, *Fixed duration* or *Fixed budget* → **Start** (Enter). The customer is optional: leave it empty for walk-ins, or type a name or phone to attach or create one.
3. Click a running card → session drawer: live timer, running total, pause history, items (− / +), and a product catalog. Click a product to add it; stock goes down at once.
4. **End session & bill** → final bill → Cash / Card / Other → amount received (change is computed) → **Confirm payment**. The session closes, the payment and receipt are stored, a receipt prints if enabled, and the station is available again.

**Customer can't pay everything?** On the bill, tick **Pay the rest later (credit)**, type what they pay now (0 is fine), and pick or create the customer (just type their name). The unpaid part is saved on their name. If you typed less cash than the total, a link offers to put the missing amount on credit. The **Credits** page lists everyone who owes money, how much, and since when. Select a customer to see their history and **Record payment** when they pay back (all or part). The customer box shows "owes …" whenever that customer is picked again, so you remember at their next visit. Admins can also add old debts from a notebook. Customers with a balance cannot be deleted.

**Discounts:** paying less than the total is allowed. Type what the customer actually pays (e.g. 300 on a 305 bill) and the difference shows as **Discount** (amber). The bill closes with that discount, which is not owed. A discount of half the bill or more asks for confirmation. Discounts appear on the receipt, the dashboard and reports ("Discounts given"). Revenue counts what was really paid. If the customer should pay the difference later instead, click *Owe it instead → put the rest on credit*.

**Split payment (+):** when several people share a bill, type what the first person pays, then press **+ Add another payment** for each other person, choosing Cash, Card or Other on each line. The panel shows "Paid so far" and "Still to pay". Change is only given back in cash. If it still doesn't reach the total, tick *Pay the rest later*. Receipts list each payment, and the Sales Cash/Card/Other totals count each part under its own method.

**Pictures:** in *Gaming Stations* (station editor) and *Products* (product editor), click **Upload picture…**, click the image box, or drag a picture onto it: a JPG/PNG file from Explorer, or a picture straight from Chrome/Edge (even without internet when the browser provides the image; otherwise it is downloaded from its link). Pictures are resized and stored locally, then shown on the dashboard cards, lists and product catalog.

**Sell products without a session:** click **Sell products** in the top bar (or press **F2**) on any page. Pick products, take the payment (split, discount or credit all work the same), done. The sale appears in Sales and Reports, and stock goes down.

Right-click a card to pause/resume, add a product, end and bill, reserve, clear a reservation, or set/clear maintenance. The **List** view (top right) shows the same with inline Pause / + Product / End / Start buttons. **Ctrl+K** searches stations, products, customers and receipts. **Esc** closes the top dialog.

### Session modes and billing
- **Open:** the customer pays for the actual play time (pauses excluded).
- **Fixed duration:** the customer buys N minutes and pays for them even if they leave early. A warning shows N minutes before the end (default 10). When time is up the card turns red. Play does **not** stop unless *Auto-end* is on in Settings. If they overstay, the extra time is billed at the same rate.
- **Fixed budget:** the customer gives an amount, and the max play time is budget ÷ rate. The charge never exceeds the budget. Top up via *Extend → Add money*.
- **Extend / change mode:** add time, add money, or switch mode at any time. Time already played is kept.
- **Reports:** besides revenue per day and station, top products and session modes, the page shows average ticket, play hours and occupancy, customers (new, walk-in), counter sales, discounts and credit left unpaid. It also shows busy hours, revenue by weekday, payment methods (including credit), revenue by room and console type, a station usage table with occupancy, product categories, top customers (with what they owe) and money collected per operator. *Stations CSV* exports the usage table, and *Export PDF* includes every section.
- **Remembered screens:** the dashboard's filter, sort and grid/list view, the Sessions, Sales and Reports periods, the Products category and low-stock filter, the Credits "settled" option and the last Settings section are saved in `ui-state.json` in the data folder, so they come back after a restart. Theme and language are saved in the settings.
- **Printing:** receipts are not printed unless you tick *Print thermal receipt* on the bill. To print every time again, use Settings → Receipts & printer.
- **Charge a customer without a session:** on Customers, select a customer, then use *Charge to credit*. **Products** opens the sale screen already set to *Pay the rest later* for that customer, and stock goes down. **Amount** records something that isn't in the product list, with a reason. The Credits page has the same two buttons (*Charge products*, *Add amount*). Operators can add charges; only an admin can reduce or forgive a debt.
- **Rooms:** Gaming Stations → *Rooms* lists every room with its number of stations. Add a room, rename it (its stations follow the new name), or delete it. If the room still has stations, you choose another room for them, or none. The station editor's *Location* list shows these rooms.
- **Price by controllers:** set on each station (Gaming Stations → edit): *controllers included* (e.g. 2) in the hourly price, *maximum controllers* (e.g. 4) and *extra per controller / h* (e.g. 100 DA). When starting a session the operator picks the number of controllers, and the price follows (2 → 300, 3 → 400, 4 → 500 DA/h). During play, use − / + in the session drawer: the new rate applies only from that moment, earlier time keeps its rate, and the bill and receipt show each part. Budget sessions recompute the time left at the new rate. Fixed-duration sessions charge the remaining purchased time at the new rate. Demo PlayStation and Xbox stations come set up this way (2 included, up to 4, +100 DA/h). **One price per console type:** in Gaming Stations → *Manage types*, set *Extra controller price / h* on a type (e.g. PlayStation 150 DA). Every console of that type uses it; tick *Also remove the stations' own prices* to make them all the same. The price is chosen in this order: the station's own price (optional, in the station editor), then the type price, then the default in Settings → Billing.
- **Pause / resume:** the timer and cost stop, and every pause is stored with its start and end.
- The **price is locked** when the session starts. Changing a station's price only affects new sessions, and every change is recorded in the station's price history.
- **Billing rules** (Settings → Billing): exact time, per minute, per 5 / 15 minutes, or per hour. Rounding can be up, nearest or down. Also set a minimum charge and a final amount rounding step (e.g. 5 DA). The rules are copied onto each session at start.
- All money is `decimal`. No floating point is used for amounts.

### Admin
- **Gaming Stations:** add, edit, change price, change image, disable/enable, delete (soft). **Manage types** adds new categories (e.g. "VR") without touching code. Disabled or deleted stations keep their history.
- **Products:** categories, purchase and selling price, margin, stock and minimum stock, image, receive stock, stock count adjustment, disable, delete (soft). Every stock change is written to `StockMovements`.
- **Reports:** daily, weekly, monthly, yearly or custom. Shows total, gaming and product revenue, estimated product profit, sessions, average duration, revenue per day or month, revenue per station, top products, session-mode split, busiest hour and payment methods. Export to CSV (Excel), or to PDF via the print dialog.
- **Users:** Admin / Operator roles. There must always be at least one active admin.
- **Settings:** center name, logo, address, phone, currency, billing rules, receipt printer and paper width, footer, warning thresholds, auto-end, long-session alert, low-stock alerts, theme (dark/light, also the sun/moon button in the top bar), language (English, Français, العربية; also from the user menu at the bottom of the sidebar, and the app restarts), and automatic daily backup (hour, folder, how many to keep). Also *Back up now* and *Restore* (with confirmation, a safety copy, and an automatic restart).

### Roles

| | Admin | Operator |
|---|:-:|:-:|
| Start/pause/extend/end sessions, add products, take payments | ✓ | ✓ |
| Dashboard, session history, sales, customers | ✓ | ✓ |
| Counter sales (products without a station) | ✓ | ✓ |
| Stations, products, prices, stock | ✓ | — |
| Reports, users, settings, backup/restore | ✓ | — |
| Delete customers | ✓ | — |

Admin-only rules are enforced in the service layer, not just hidden in the UI. Payments and completed sessions are never deleted.

### Receipts, PDF and export
- Receipts print through Windows printing. Pick a thermal printer and paper width (58/80 mm) in Settings for silent printing. Choose **Microsoft Print to PDF** to save a PDF.
- Reprint any receipt from **Sessions** or **Sales**.
- CSV exports (UTF-8 with BOM, open directly in Excel): sessions, sales, customers, products, report per day.

### Notifications
Session ending soon, time up, long session (default 4 h) and low stock each fire **once** per event, as a toast plus an entry in the bell menu. They are never repeated every second.

---

## Architecture

```
GamingCenter.sln
├── src/
│   ├── GamingCenter.Domain/          Entities, enums, billing rules & calculator (pure, no dependencies)
│   ├── GamingCenter.Application/     Service interfaces, DTOs, settings model, money/duration formatting, CSV writer
│   ├── GamingCenter.Infrastructure/  EF Core DbContext, migrations, seed, service implementations, backup, DI registration
│   └── GamingCenter.App/             WPF: Views (XAML), ViewModels, theme, app services (timer, dialogs, toasts, printing, images)
├── tests/GamingCenter.Tests/         xUnit tests (billing + services on SQLite)
├── scripts/                          publish.ps1, add-migration.ps1
└── design/                           Claude Design handoff (HTML mockups + chat)
```

- **Business logic stays out of the views.** Session maths lives on `GamingSession` and in `BillingCalculator` (Domain). Rules and transactions live in `SessionService`, `StationService` and the other services (Infrastructure). View models only call services.
- **Transactions** wrap starting a session, adding or removing products (stock and session together), completing a session with its payment, cancelling, counter sales, stock receipts and price changes. If payment validation fails, nothing is written.
- **Timers:** one `DispatcherTimer` for the whole app (`TickService`). Every card recomputes its timer and cost from stored timestamps once per second, with no thread per station, so 50+ stations cost the same as one. Timers survive restarts because the state is timestamps in the database.
- **Short-lived DbContexts** via `IDbContextFactory` (recommended for desktop apps). Indexes on session status, start time and station, payment date, receipt number, product and station names.
- **Images** are decoded with WPF, downscaled to ≤ 800 px and re-encoded (JPEG, or PNG if transparent). Lists load cached thumbnails (`DecodePixelWidth`), never full-size files.
- **SQLite and decimals:** EF stores `decimal` as TEXT, which is exact but cannot be summed in SQL. Reports filter by indexed dates in SQL and aggregate in memory, which is fast for many thousands of sessions.
- **Future multi-computer version:** the UI depends only on the `Application` interfaces. A networked version can swap `Infrastructure` for a server- or API-backed implementation (or point EF at a server database) without touching view models.

### Entities
`CreditTransaction` (customer credit ledger: unpaid bills +, repayments −), `User`, `GamingStationType`, `GamingStation`, `PriceHistory`, `Customer`, `ProductCategory`, `Product`, `StockMovement`, `GamingSession` (also used for counter sales, with no station), `SessionPause`, `SessionProduct` (name, price and cost are copied at sale time), `Payment` (one per session, and it is the receipt: number `yyyy-MMdd-NNN`), `ApplicationSetting` (key/value).
Roles are an enum on `User`. Daily reports are computed on demand rather than stored, so they can never go stale.

### Not in version 1
Customer accounts, loyalty, prepaid balances, online reservations, QR codes, remote monitoring and PC locking (see §46 of the brief). The layering above keeps room for them. The UI is available in English, French and Arabic (right-to-left). Translations are in `src/GamingCenter.App/Resources/Lang/{fr,ar}.json`, keyed by the English text. Any text that is missing a translation stays in English.
