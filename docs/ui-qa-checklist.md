# Inventory Manager — UI and camera acceptance checklist

These tests are required before calling a deployment production-ready. Automated CI catches syntax, CSS balance and missing DOM references, but cannot prove camera performance or mobile layout.

## Sign-in and navigation
- [ ] First-time employee entering an unused employee ID is guided to the activation form; matching email and password are required twice.
- [ ] Password policy enforces 8+ characters with uppercase, lowercase, digit and symbol on client and API.
- [ ] Verification email contains a one-time 24-hour link; it activates exactly once and cannot be reused.
- [ ] Forgot password sends a 30-minute link to verified email, gives identical responses for unknown IDs and invalidates old sessions.
- [ ] Global administrator can configure the sending Gmail account with their own password and verify the OWNER bootstrap email under Settings.
- [ ] No auto-signout countdown is displayed; real user interaction keeps sessions alive while background inventory polling does not; sign out after 15 minutes idle.

- [ ] On iPhone Safari, Android Chrome, and desktop Chrome/Edge, employee ID + individual password sign-in opens Home without flashing back to login.
- [ ] OWNER initial setup uses the installation token with matching email/password fields, then password sign-in opens Home; the Administration tab appears only for authorized users.
- [ ] Mobile bottom navigation contains Home, Items, In, Out, History and role-based Admin without clipping at 320, 375, 390 and 430 CSS pixels.
- [ ] The mobile account header has a working Settings and Sign out button.
- [ ] Silent 15-minute inactivity expiry signs out after genuine inactivity; normal interaction renews the session; background polling does not.
- [ ] Changing store updates the location heading, products and permissions.

### Employee display-name edits

- [ ] Store admin sees Rename for themselves and ordinary employees assigned to their selected store, including secondary-store members.
- [ ] Store admin cannot rename another store admin or global admin (both UI and API).
- [ ] Store admin cannot rename an employee without access to the currently selected store (API rejects).
- [ ] Global admin can rename ordinary employees, store admins, other global admins and themselves.
- [ ] Blank, whitespace-only, over-100-character, and control-character names are rejected.
- [ ] Renaming updates the displayed employee name without changing employee ID, login, store access, prior inventory attribution or sessions.
- [ ] Audit history records display-name changes with actor, target and old/new names.

### Account changes and renamed owner

- [ ] Settings → Account security allows employees to enter a new email twice and confirm their current password.
- [ ] Wrong password, unmatched emails and duplicate addresses are rejected without changing the current verified address.
- [ ] Email changes use a one-time **30-minute** link delivered to the *new* address; the old address remains active until confirmation.
- [ ] Confirmation changes the account email, invalidates existing sessions, and requires a new sign-in.
- [ ] Ordinary employees and store administrators cannot change their own or another employee's ID (server returns 403).
- [ ] Global administrator can rename a staff ID; store access, transaction attribution and login credentials still work under the new ID; old sessions are revoked.
- [ ] Original owner can rename `OWNER` to a new ID, log back in as that ID, retain global privileges and remain unable to demote the recovery-owner account.
- [ ] Restart and `/api/auth/status` after an OWNER rename: app remains initialized and does not create a second OWNER account.

## Signed-in ambient animation

- [ ] A clearly visible but slow animation is visible across the background and subtly behind translucent cards/sidebar, without reducing the readability of tables, forms, sidebar, camera UI or modal dialogs.
- [ ] Pointer and touch movements do **not** change the signed-in animation; only the login-screen aurora is interactive.
- [ ] Each personal theme changes the ambient colors immediately, including a restrained light-mode version for Glacier Light.
- [ ] Animation starts after sign-in, stops on logout and while the tab is hidden, and resumes on return.
- [ ] With Reduce Motion enabled, motion stops and a static ambient gradient remains.
- [ ] Large inventories, tall history pages and mobile layouts do not create oversized canvases; scrolling stays responsive.

## Original sign-in aurora and accessibility

- [ ] Login First-time setup/Forgot password and mobile Settings/Sign Out buttons use the active theme rather than fixed gray, red, or blue colors.
- [ ] Login canvas glows and signed-in drifting clouds are visibly stronger while forms, mobile labels, and table content remain readable.

- [ ] Sign-in, first-time setup, account activation, and password reset show theme-matched drifting soft glows behind the login card.
- [ ] Moving the mouse creates a short, soft colored trail that expands and fades; movement works without clicking or dragging.
- [ ] Sidebar and mobile **Sign Out** buttons follow each active theme instead of fixed warning colors.
- [ ] All ten global login themes update the glow colors, including a restrained effect on Glacier Light.
- [ ] The animation does not block clicks, keyboard navigation, autofill, or login submissions.
- [ ] The animation stops after sign-in, resumes on logout, and pauses when the tab is hidden.
- [ ] Reduce Motion suppresses animation and keeps a readable theme-matched static background.
- [ ] Test desktop browsers and iOS Safari, including touch, resizing, and performance.

## Mobile theme consistency

- [ ] On Deep Purple, mobile Recent Activity, Recent Inventory, checkout history, reorder cards and audit entries use purple-toned surfaces rather than navy/blue.
- [ ] Switching among all ten themes recolors these inner mobile cards and the bottom navigation bar consistently.
- [ ] Glacier Light retains readable pale mobile cards with dark text; controls and note-action links remain visible.

## Ten themes and brand identity

- [ ] Evergreen, Deep Purple, Midnight Ocean, Copper Ember, Rose Noir, Arctic Slate, Forest Night, Champagne Gold, Crimson Night and Glacier Light are available under Settings → Appearance for **every** authenticated employee.
- [ ] Each selection applies immediately to backgrounds, panels, navigation, buttons, focus states, logo and favicon; refresh/re-login preserves it.
- [ ] Separate employees can select different palettes; changing one preference does not modify another's.
- [ ] Global admin sees Administration → Login screen appearance and can save the public theme. Other roles do not see the control and get HTTP 403 from its API.
- [ ] The public theme appears on new anonymous page loads, first-time setup, password reset and sign-out. A login restores the employee's personal theme.
- [ ] Each theme's logo preserves shape/legibility; favicon and meta theme-color match.
- [ ] Glacier Light switches the browser to light color-scheme; typography, tables, tags, buttons, inputs, modals, toasts, warnings and responsive cards remain readable, without light-on-light text.
- [ ] Desktop sidebar links (Home, Inventory, Check In, Check Out, History, Administration and Settings) and mobile navigation hover/focus use the selected palette rather than a fixed charcoal/green background.
- [ ] Search field background, border, placeholder and focus/hover styling follow the selected palette on every theme, including Glacier Light.
- [ ] Desktop inventory rows and responsive/mobile product cards use theme-colored hover highlighting instead of the old hardcoded charcoal/white overlay.
- [ ] Glacier Light uses a dark-on-light logo; dark themes retain light-on-dark logos.
- [ ] Verify readability, keyboard focus, contrast and iOS Safari/mobile navigation across all ten palettes.
- [ ] Audit log includes `theme.login_changed`; no inventory or account permission changes result from changing appearance.

## Responsive display
- [ ] Test 320, 375, 390, 430, 760, 1024 and 1440 px viewports.
- [ ] All four overview metric cards fit without horizontal page scroll.
- [ ] Recent inventory, recent activity, all inventory, transaction history and store administration lists do not overflow.
- [ ] Long SKUs, product names, employee names and notes wrap without covering actions.
- [ ] Full-width buttons and inputs remain tappable when iPhone keyboard is open.
- [ ] Modal forms, product deletion confirmation and edit store location are usable in portrait and landscape.
- [ ] Keyboard Tab/Shift+Tab stays in a modal and Escape dismisses it; focus returns to the trigger.
- [ ] Search and stock filters update the inventory count and do not alter stock.

## Camera barcode reader
- [ ] Grant camera permission on iPhone Safari; rear camera opens once without switching streams for ZXing fallback.
- [ ] Scan a UPC-A and EAN-13 retail barcode; result opens the correct product on first recognition.
- [ ] Scan Code 128 and Code 39 inventory labels and a QR code where applicable.
- [ ] In good lighting, test barcode recognition at multiple distances; try a reflective package.
- [ ] Where supported, Turn on flashlight / Turn off flashlight works without restarting the camera.
- [ ] On a browser supporting native BarcodeDetector, detection starts promptly and stops at first result.
- [ ] On Safari or a browser without BarcodeDetector, ZXing starts without a second camera permission request.
- [ ] Manual entry closes camera tracks and places focus in the barcode search field.
- [ ] Closing the dialog, navigating away, switching browser tabs, or signing out releases the camera.
- [ ] Poor network or blocked CDN displays a useful compatibility decoder error and manual entry remains available.
- [ ] Retry scanner after success, cancellation, camera-denied error, or reopening the dialog.

## Administration / email
- [ ] Store admins see recipient settings but cannot see the global Gmail/OAuth configuration.
- [ ] Global admin can connect the one sending Gmail account and send a test notification for each store.
- [ ] An invalid or oversized OAuth client JSON is rejected without exposing a secret in the interface.
- [ ] A valid Google OAuth import works; returning from Google does not trigger a spurious logout.
- [ ] Long recipient addresses wrap, and all admin forms fit within the mobile viewport.

## Inventory reporting and administrative activity
- [ ] Export inventory CSV from a populated store; verify every active SKU appears, including beyond the first page of displayed inventory
- [ ] CSV fields with commas, quotes or formula characters open as text without executing formulas
- [ ] Click the low-stock count and confirm it opens the reorder list for the selected location
- [ ] Check that a product at the minimum threshold suggests ordering one unit and one above the minimum no longer appears
- [ ] Export the reorder CSV; confirm current/minimum/order quantities and store isolation
- [ ] Open Edit → View history, verify stock movement order, employee, before/after values and reason
- [ ] Toggle product history while the edit form contains unsaved changes; verify those fields are retained
- [ ] Use +1, +5, −1, −5 quick adjustment presets; verify checkout reason, explicit Save and no negative balance
- [ ] Review admin activity after granting/removing access, editing roles, changing alert preferences and deleting a product
- [ ] Confirm store admins only see their store's admin activity while global admins see activity across all stores
- [ ] Rename a store and ensure its existing activity history remains linked to the new store ID

## Checkout follow-up notes
- [ ] Check out a product for repair, then open **History** and click **Edit note** on that checkout
- [ ] Add a follow-up such as "Damaged during repair" and save; verify it appears immediately in history
- [ ] Open the product's **Edit → View history** and confirm the new note appears there too
- [ ] Verify checkout quantity, before/after balances, original checkout timestamp and original employee are unchanged
- [ ] Edit the same checkout twice, then open **View changes** to inspect previous values, revision author and timestamp
- [ ] A standard employee can update both their own checkout and another employee's checkout in their currently selected store; verify editor attribution in View changes
- [ ] Store/global administrators can edit a checkout note within an authorized store
- [ ] Attempt to edit a check-in note, a different store's checkout, or a stale note revision; verify rejection
- [ ] Confirm the note editor cannot save blank notes or more than 1,000 characters
- [ ] Confirm a note edit inside a product modal does not save or overwrite unsaved product fields
- [ ] Confirm editing a note as admin creates an activity-log entry without copying the note text

## Release readiness beyond visual checks
- [ ] CI .NET Release build passes.
- [ ] Validate schema migrations against a copy of production PostgreSQL.
- [ ] Take and restore a backup to confirm the database and encryption key can be recovered.
- [ ] Confirm private environment variables are not included in Git or sent to browsers.
- [ ] Limit first-claim activation of unused employee IDs to trusted staff, and verify no employee/store-admin can sign in without a password.
- [ ] Verify Gmail authorization and automatic alert delivery in the intended Google OAuth publishing mode.
- [ ] Check accessibility with keyboard and screen reader, and test on real iOS Safari hardware.
- [ ] Monitor logs and browser console for 4xx/5xx failures during a smoke-test session.
