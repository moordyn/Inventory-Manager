# Architecture and product requirements

## Stock model
An item has a unique SKU, name, optional description/category, minimum quantity, and one or more unique barcodes. Inventory movements are append-only events: check-in, check-out, and adjustment. Every event records before/after balances, quantity, timestamp, optional note and idempotency key. The server must enforce nonnegative stock and atomic updates in the database.

## Client workflow
Scan known barcode -> show product and balance -> choose in/out and amount -> confirm -> receive updated balance. Unknown barcode -> open item creation with scanned code prefilled. Allow manual entry.

## Security
One shared PIN/passphrase; never expose the database or API without authentication. Hash credentials using a password KDF, use secure cookies or bearer session tokens, rate-limit login, and enforce HTTPS at Nginx. Never commit passwords. Note that shared credentials cannot identify individuals in audit history.

## Network
Public DNS `inv.mrmschf.xyz` follows existing `mrmschf.xyz` DDNS, Nginx terminates TLS and proxies to internal Linux VM port 18424. PostgreSQL remains on private Docker network. Forward only 443 (and 80 for ACME if necessary) to Nginx; do not forward 8080 or 5432 publicly.

## UX
Dark charcoal background, slightly lighter cards, white/gray text, muted green accents, responsive design. Windows-first keyboard/scanner workflows; iOS touch and camera workflow.

## Reliability
Online-only. Every mutation uses a unique idempotency key; use database transactions and row locks to prevent overselling under concurrency. Support daily database backups and tested restores. Reconnect clients after network interruptions.

## Out of scope
Employee accounts, invoicing, accounting, multiple warehouses, offline edits.
