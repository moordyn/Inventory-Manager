# Accounts, recovery and permissions

[Documentation index](COMPLETE_DEPLOYMENT_GUIDE.md) · [Deployment](deployment.md) · [Operations](operations.md)

## Identity model

An **employee ID** is the login name and is globally unique across stores. Employees authenticate with a personal password; shared PIN and ID-only sign-in are retired. Passwords require 8–128 characters with uppercase, lowercase, number and symbol. The server stores salted PBKDF2-HMAC-SHA256 hashes (600,000 iterations). Sessions expire after **15 minutes of inactivity**; background polling does not reset the idle timer.

| Action | Employee | Store admin | Global admin |
| --- | --- | --- | --- |
| Login, change own verified email | Yes | Yes | Yes |
| Create employee for an authorized store | No | Yes | Yes |
| Manage assigned store permissions / access | No | Limited to authorized store | Yes |
| Change own display name | No | Yes | Yes |
| Rename other ordinary employees at an assigned store | No | Yes | Yes |
| Rename another administrator's display name | No | No | Yes |
| Change any employee ID (including own) | No | No | Yes |
| Promote/demote global admins | No | No | Yes |
| Rename original owner ID | No | No | Yes |
| Demote original recovery-owner account | No | No | **Blocked** |

The original recovery owner's account **starts with ID `OWNER`** and is bootstrapped using the one-time install token. Its identity is persisted in `settings.owner_employee_id` so a global administrator can rename that ID, including their own, without causing setup to re-open or the system to create another owner on restart. Its global privilege remains protected from demotion, even after rename.

## Initial owner bootstrap

1. Install and configure `PUBLIC_ORIGIN`; open the app over HTTPS.
2. Enter the `SETUP_TOKEN` generated in the private installation `.env`/installer output.
3. Enter the owner's email twice and password twice; sign in as `OWNER` initially.
4. Connect the global Gmail sender (or configure SMTP), then visit **Settings → Account security** to request/complete mailbox verification.
5. Only after verification and onboarding checks, provide staff access.

Legacy v1.0.0 PIN/ID-only sessions are invalidated during the one-time password-auth migration. Existing stock and history are not intentionally deleted.

## Create and activate employees

Administrators create an employee's **ID, name, primary store**; no password is entered by the administrator. The employee chooses **First-time setup**, enters the ID, email twice, and password twice. The server sends a single-use **24-hour** activation link to that email. Verification activates the account; regular login then uses ID + password.

**Risk:** an unclaimed ID may be claimed by the first person to register it. Restrict first-time activation to trusted networks or an identity-aware gateway; verify the intended employee's enrollment before opening public access. A correct password is not a substitute for verified initial identity.

## Change email or reset password

**Change own email:** Settings → Account security → Change your email address. Enter the **new email twice** and current password. A single-use verification link is sent to the new address and expires after **30 minutes**. Until the link is confirmed, the existing verified email remains active for password recovery. On confirmation, the new email becomes verified, pending tokens are cleared, and existing sessions are revoked; sign in again. An unverified change does not replace the old address.

**Forgot password:** From sign-in, enter the employee ID. If it has a verified email, a **30-minute single-use** reset link is sent there; the response does not disclose whether an account exists. Choosing a valid replacement password invalidates old sessions and login credentials. Contact an administrator if the mailbox cannot be accessed; do not bypass verification by directly overwriting hashes.

**First-run owner verification:** The provisional recovery owner can sign in while the global sender is being configured; verify the mailbox promptly before public use.

## Rename employee display names — role scoped

Open **Administration → Team & permissions**, find an employee, and choose **Rename**. Enter the replacement display name (1–100 characters). This does **not** alter the employee's permanent ID, login, passwords, session, inventory history or store grants.

- **Store administrators** can rename **themselves**, plus non-administrator employees who have access to the store they are currently managing (including employees whose primary location is another store). They **cannot** rename another store administrator or global administrator, even if that person is assigned to the same store.
- **Global administrators** can rename **any employee**, including themselves and the original recovery owner.
- **Ordinary employees** cannot rename themselves or anyone else.

The API enforces these permissions independently of whether a button is visible. Successful changes are recorded in the administrator audit log. Display-name changes appear on the next reload; employees who rename themselves see the new name immediately.

**Display names and IDs are different:** Only global administrators can change employee IDs. The rules below continue to apply to IDs.

## Change employee ID — global admins only

1. Open **Administration → Team & permissions** and select the employee's **Manage access** panel.
2. Change the Employee ID (2–24 letters, numbers, underscores or hyphens), confirm the rename.
3. The backend authorizes **global administrator only**, checks uniqueness, performs a database transaction updating employee and related references (store access, movement history, note editors, account tokens, OAuth ownership and other stored attributions), and revokes the renamed account's sessions.
4. The renamed user signs in with the **new ID**, same password, and existing verified email.

A global administrator may rename **their own** ID, including the original `OWNER` ID. Renaming the recovery owner updates the persisted owner identifier so initialization and recovery protections continue working. Avoid simultaneous changes to the same account and verify the identity/history afterward. Store admins and ordinary employees have no ID-edit API permission.

## Locations and administrative audit

- Primary-store membership is retained when secondary access is revoked.
- Global admins can manage stores and global roles; store admins have scoped permissions.
- Switching location changes which stock, movements, roles and alerts are shown.
- Audit logs record administrative changes; access and email changes are not the same as stock movements.
- On departure, revoke access and review historical activity; avoid reusing employee IDs.

## Account email sending

Verification, activation, password reset and email-change links use the central outgoing mail sender: global Gmail API integration or configured SMTP. The link hostname is derived from `PUBLIC_ORIGIN`; if the domain or Google credentials change, retest **all** mail workflows. Store low-stock recipient lists do not control employee security email delivery.

For networking and OAuth setup see [Deployment](deployment.md). For diagnosis and recovery see [Operations](operations.md).
