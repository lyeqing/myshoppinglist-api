# MyShoppingList User Guide

MyShoppingList lets you collect products from **Coles, Woolworths and ALDI**, organise shopping lists, and plan your shop using saved price observations.

This guide reflects the current application code and extension instructions. The running installation was not tested while preparing this guide. Some features depend on the installation's configuration and your account type.

## 1. Start your account

Open MyShoppingList and choose:

- **Create an account** to keep your lists.
- **Sign in** if you already have an account.
- **Start my shopping list** for a temporary three-hour trial.

If you start with a trial, use **Keep my list** to register before it expires. Expired trials cannot be converted.

## 2. Create or select a list

Under **My lists**:

1. Enter a name under **New list name**, or leave it blank for an automatic name.
2. Click **Create new list**.
3. Use **Selected list** to switch between lists.

Free registered accounts support two active lists; paid accounts support five.

## 3. Set up the Chrome extension

Adding new retailer products requires Chrome on your computer. You can manage saved lists on mobile.

The extension is currently a local test build, with no public Chrome Web Store listing. For your local project:

1. Open `chrome://extensions` in Chrome.
2. Enable **Developer mode**.
3. Choose **Load unpacked** and select `D:\pra\myshoppinglist\extensions\shopping-list`.
4. Pin **MyShoppingList — Add to list (Local test)**.

The API must also allow your extension's ID and have personal capture enabled. If that setup is already complete, you only need to enable the extension and sign in using the same Chrome profile.

## 4. Add products

1. Open a specific product page on Coles, Woolworths or ALDI.
2. Click the MyShoppingList extension.
3. Choose the destination list and quantity.
4. Click **Read product**.
5. Review the name, pack size, price and conditions.
6. Click **Save to list**.

You can save a product even when its price is unavailable. If the save result is uncertain, use **Retry save** to resolve the same request.

To collect prices from another retailer, visit that retailer's product page and repeat the process.

## 5. Organise your list

On **My lists**, you can:

- Change quantities with **−** and **+**.
- Choose **Add note** or **Edit note**, then **Save note**.
- Mark a product **Favorite** for easy reuse.
- Mark a product **Essential** to automatically add one to each new list.
- Choose **Delete** to remove an item.

Under **Products**, browse your product history, Favorites and Essentials, and add saved products to a selected list.

## 6. Refresh saved prices

With the extension enabled, click **Refresh items** on the shopping page.

The extension reads known retailer product pages in temporary tabs and updates saved observations. Purchased and hidden items are skipped.

- **Cancel refresh** stops further checks and retains completed updates.
- **Retry failed items** retries unsuccessful checks.
- **Retry unfinished items** resumes items left unresolved after an interruption.

If a retailer blocks a page, open the product yourself, complete any verification, and try again.

## 7. Use the list while shopping

Open **Shop in store**, including on your phone:

1. Select your list.
2. Review the remaining products, quantities and notes.
3. Tick **Purchased** as you collect each item.
4. Use **Show purchased** to see completed items and untick mistakes.

The **Plan your shop** section shows estimates based on eligible saved observations. Check how many items have prices: an **Incomplete basket** is only a subtotal.

## 8. Finish your shop

Paid accounts can use **Archive list** and find it later under **Archived lists**. **Delete list** permanently removes a list and does not preserve it in History.

## Understanding saved prices

Saved prices record what was observed at a particular time. Refreshing updates those observations, but prices, stock, promotions and store differences still need confirmation with the retailer.

## Typical shopping routine

Create a list → save products with the extension → adjust quantities → refresh prices → open **Shop in store** → tick purchases.


## Automatic comparisons after adding from Coles

Adding a Coles product saves it first, then the extension searches Woolworths and ALDI. Read product is optional; checks start when you save, not when you preview. Keep Chrome open while checks run. Exact matches can contribute to your list totals; different brands, sizes and uncertain matches are labelled alternatives and excluded from exact totals. Check the product links and dated prices before shopping. Missing prices remain unknown.

Use Full view or your shopping list for results. Cancel checks keeps your saved item and completed results. Retry failed checks checks unsuccessful retailers again. If verification is required, choose Open retailer, complete it yourself, then Resume checks. After restarting Chrome, open the extension and resume outstanding checks. Signing out stops private work.
