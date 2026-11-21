# Repair Queue

The **Repair Queue** page tracks device repairs separately from inventory receiving and checkout. The queue shows active tickets oldest check-in first, with ticket number, who checked it in, device, repair type, total quote, upfront payment, outstanding balance, technician, and the number of consumed parts. Completed repairs remain in the active queue until paid/picked up; paid/picked-up and cancelled repairs remain in a separate history view.

## Check in a repair

Sign in to the appropriate store, open **Repair Queue → Check in repair**, and enter the device and repair type. The total quoted amount and the amount already paid upfront are required, even if zero. Customer name, contact, serial/IMEI, and notes are optional. The employee who checks in the device is automatically assigned as its technician; there is no manual technician selector or reassignment action. The application automatically records the current store, employee ID and time.

All money fields are recorded values only: **this application does not charge, refund or authorize payments**. The initial deposit is captured as a recorded amount at check-in. Once repair work is marked **Ready for Pickup** or **Completed**, a single **Mark Paid / Picked Up** action records receipt of the entire remaining balance and the physical pickup together. The button becomes available at either status; going through Completed is not required. The balance is \`quoted_total - paid_upfront - sum(recorded final/legacy payments)\`. This action is idempotent: retries never record another payment or pickup. If the deposit already covers the quote, pickup records $0 and still closes the ticket. No payment provider, tax calculation or invoicing is introduced.

## Status and order

Active statuses are **Checked In**, **In Progress**, **Waiting for Parts**, **Ready for Pickup**, and **Completed (Awaiting Pickup)**. Ready for Pickup and Completed tickets remain in the active queue until the final balance is recorded and the device is picked up. **Mark Paid / Picked Up** moves the completed ticket into history, alongside cancelled tickets. A picked-up ticket cannot be reopened or reassigned to another status. All changes record the employee and timestamp in the ticket's activity timeline.

Active tickets are sorted ascending by check-in timestamp and ticket ID, not the latest update time. Each location sees only its own tickets. The global/store administrator permission model and the existing session authorization apply.

## Scan and record parts

On a ticket, choose an inventory product by name/SKU, scan a barcode through the existing camera scanner, or enter a SKU/barcode manually. Set the quantity (1–10,000), then **Deduct & add**. Repeat for as many different products or quantities as needed.

The server performs all operations in **one PostgreSQL transaction**:
1. Confirm the employee's store and verify the ticket is open.
2. Lock the ticket and product stock row.
3. Verify sufficient stock.
4. Update the product quantity, insert a normal inventory checkout movement, and insert a repair-part link.
5. Add the employee-attributed repair activity event and update low-stock alerts.
6. Commit. Failure rolls back every step.

Each part scan requires an idempotency UUID. Repeating a request with the same UUID and same part/quantity/ticket returns the original result without a second stock deduction. Mismatched reuses are rejected. The original movement and repair-part ID remain linked permanently.

**Return stock** reverses the complete recorded quantity of that part entry, not an arbitrary partial amount. This action records a separate positive inventory movement, return employee, timestamp, and the original link. Repeated return requests cannot add inventory twice. To return a portion of a multi-unit part entry, use a separate part entry per unit initially, or document a general stock correction; partial returns are not offered in the first release.

Do not use the general inventory checkout and the repair-part action for the same physical part: the latter already performs the inventory checkout.

## API and storage

The server adds \`repair_tickets\`, \`repair_parts\`, \`repair_payments\`, and \`repair_events\`. The installer/startup database migration creates these tables automatically. Existing \`items\`, \`movements\`, \`low_stock_state\` and low-stock notifications remain authoritative.

| Route | Purpose |
|---|---|
| \`GET /api/repairs?history=false\` | Active queue, oldest first |
| \`GET /api/repairs?history=true\` | Paid/picked-up and cancelled history |
| \`GET /api/repairs/{id}\` | One ticket with parts, payments and audit timeline |
| \`POST /api/repairs\` | Check in a repair, with an idempotency key |
| \`PUT /api/repairs/{id}/status\` | Update status |
| \`POST /api/repairs/{id}/pickup\` | From Ready for Pickup or Completed, record the entire remaining balance and customer pickup, idempotently |
| \`POST /api/repairs/{id}/parts\` | Consume inventory part and link movement, idempotently |
| \`POST /api/repairs/{id}/parts/{partId}/return\` | Return unused part quantity to stock, idempotently |

Users are authenticated via the existing bearer session and cannot inspect or change another store's repair tickets. Inventory adjustments remain consistent with normal checkout history, low-stock reporting and permissions.

## QA checklist

- Create two repairs and confirm oldest-first order, author attribution, and remaining balance.
- Check in a repair with an upfront payment; verify it cannot exceed the quote.
- Scan at least two different parts; confirm their product quantities decrease and history contains both linked checkout events.
- Repeat the exact same scan request UUID; verify no second deduction. Attempt stock use beyond availability; verify no repair entry or stock movement was saved.
- Return a part, repeat the return, and verify inventory increases exactly once.
- Mark one repair Ready for Pickup (skipping Completed) and another Completed; verify both stay in the active queue with the final pickup action. Confirm **Mark Paid / Picked Up**, ensure the entire balance is recorded once, then verify it moves to history and cannot be reopened.
- Switch stores and confirm no ticket leakage. Verify a non-administrator employee can check in and update their own store's tickets.
- Test desktop and mobile browsers, barcode camera permissions, focus/keyboard access, theme colors and reduced-motion behavior.

The automated integration suite (\`tests/api-smoke.mjs\`) verifies creation, ordering, multi-part usage, retry safety, stock history, return, payments, closure, and cross-store isolation.
