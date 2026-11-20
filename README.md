# Inventory Manager

Self-hosted barcode inventory management for Windows, web, and iOS.

## Architecture
- **Server:** ASP.NET Core 10 + PostgreSQL, hosted in a Proxmox Linux VM.
- **Clients:** Shared React/TypeScript interface, Tauri 2 Windows packaging, Capacitor iOS packaging (planned).
- **Network:** Existing Nginx proxy forwards `https://mrmschf.inventory.xyz` to the VM's port 8080. No Caddy and no public database port.
- **Authentication:** One shared passphrase/PIN, with no employee accounts.
- **Connectivity:** Online-only; the server is authoritative for stock.
- **Scanning:** USB HID keyboard scanner on Windows; iOS camera and compatible Bluetooth HID scanners.
- **Theme:** Soft-charcoal dark UI, muted gray panels, green success accents.

## Status
Phase 1: server foundation and architecture. This is **not yet a production-ready or tested Windows/iOS application**.

## Run on Linux
See [deployment instructions](docs/deployment.md). Configure `POSTGRES_PASSWORD` and `INVENTORY_PIN` in a private `.env` file. Do not commit secrets.

## Roadmap
1. API, database, atomic stock transactions and tests.
2. Windows-first scanner UI and Tauri package.
3. Proxmox Linux installer, Nginx integration, backup/restore.
4. Web UI, realtime updates.
5. iOS camera scanner and Capacitor packaging.
6. Security, concurrency and release validation.

No employee accounts, billing, purchase orders or offline sync in v1.
