# Inventory Manager — UI and camera acceptance checklist

These tests are required before calling a deployment production-ready. Automated CI catches syntax, CSS balance and missing DOM references, but cannot prove camera performance or mobile layout.

## Sign-in and navigation
- [ ] On iPhone Safari, Android Chrome, and desktop Chrome/Edge, employee ID sign-in opens Home without flashing back to login.
- [ ] Global admin PIN sign-in opens Home; the Administration tab appears only for authorized users.
- [ ] Mobile bottom navigation contains Home, Items, In, Out, History and role-based Admin without clipping at 320, 375, 390 and 430 CSS pixels.
- [ ] The mobile account header has a working Settings and Sign out button.
- [ ] Inactivity countdown reaches sign out only after 30 seconds without interaction; valid activity resets it.
- [ ] Changing store updates the location heading, products and permissions.

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
- [ ] Fix or mitigate employee-ID-only authorization before treating store-admin actions as securely authenticated.
- [ ] Verify Gmail authorization and automatic alert delivery in the intended Google OAuth publishing mode.
- [ ] Check accessibility with keyboard and screen reader, and test on real iOS Safari hardware.
- [ ] Monitor logs and browser console for 4xx/5xx failures during a smoke-test session.
