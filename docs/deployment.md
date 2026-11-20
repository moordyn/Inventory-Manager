# Proxmox / Linux deployment

Recommended: dedicated Debian or Ubuntu VM with Docker Engine and Docker Compose plugin.

1. Clone the repository to the Linux VM.
2. Copy `.env.example` to `.env`; set strong unique `POSTGRES_PASSWORD` and `INVENTORY_PIN`.
3. Run `docker compose -f deploy/compose.yml --env-file .env up -d --build`.
4. Verify `curl http://VM_LAN_IP:8080/health`.
5. In existing Nginx proxy, configure hostname `mrmschf.inventory.xyz`, upstream `http://VM_LAN_IP:8080`, HTTPS certificate, and WebSocket upgrade forwarding when realtime is enabled.
6. Point the hostname DNS CNAME to `mrmschf.xyz` (or equivalent DNS configuration).
7. Ensure Docker service is enabled at boot: `sudo systemctl enable --now docker`.

Do **not** forward port 8080 or PostgreSQL port 5432 from the router to the internet. Only Nginx should be internet-facing. Configure firewall rules so only Nginx can access 8080 if possible.

The installer, automated backups, production authentication hardening, and deployment verification are pending milestones. Do not expose this initial API publicly until authentication and security checks are complete.
