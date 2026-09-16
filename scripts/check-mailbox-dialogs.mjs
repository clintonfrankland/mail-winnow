// Run after rendering MailboxDialogRenderedTests with MAILBOX_DIALOG_FIXTURE_PATH.
// node scripts/check-mailbox-dialogs.mjs /path/to/playwright/index.mjs /tmp/mailbox-dialog-fixture.html
// Uses only a local fixture; no app credentials, mailbox access, or POST requests.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const { chromium } = await import(pathToFileURL(process.argv[2]).href);
const markup = await readFile(process.argv[3], "utf8");
const stylesheet = await readFile(new URL("../src/MailWinnow.Web/Components/Pages/Mailboxes.razor.css", import.meta.url), "utf8");
const script = await readFile(new URL("../src/MailWinnow.Web/wwwroot/js/dialogs.js", import.meta.url), "utf8");
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || "/usr/bin/chromium", headless: true, args: ["--no-sandbox"] });
try {
    for (const viewport of [{ width: 1280, height: 800 }, { width: 375, height: 600 }]) {
        const page = await browser.newPage({ viewport });
        await page.setContent(`<style>body{margin:0}*{box-sizing:border-box}.table-responsive{overflow:auto;max-height:100px}input{display:block}button{padding:8px}label{display:block}</style><style>${stylesheet}</style>${markup}`);
        await page.addScriptTag({ content: script, type: "module" });
        const openers = await page.locator("[data-dialog-open]").all();
        assert.equal(openers.length, 4);
        for (const opener of openers) {
            const identifier = await opener.getAttribute("data-dialog-open");
            await opener.click();
            const dialog = page.locator(`#${identifier}`);
            await dialog.waitFor({ state: "visible" });
            // Even a newly added maximum-z-index page layer must stay behind the modal.
            await page.evaluate(() => {
                const overlay = document.createElement("div");
                overlay.id = "fixture-overlay";
                overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;background:red";
                document.body.append(overlay);
            });
            assert.equal(await dialog.evaluate(element => element.matches(":modal")), true);
            assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true);
            const bounds = await dialog.boundingBox();
            assert(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= viewport.width + 1 && bounds.y + bounds.height <= viewport.height + 1);
            assert.equal(await dialog.evaluate(element => {
                const bounds = element.getBoundingClientRect();
                return element.contains(document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + Math.min(bounds.height / 2, 60)));
            }), true);
            for (let index = 0; index < 14; index++) {
                await page.keyboard.press(index % 3 === 0 ? "Shift+Tab" : "Tab");
                assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true);
            }
            await dialog.locator("form button").scrollIntoViewIfNeeded();
            assert.equal(await dialog.locator("form button").isVisible(), true);
            await dialog.locator("input[type=password]").fill("fixture-not-a-secret");
            await page.keyboard.press("Escape");
            await dialog.waitFor({ state: "hidden" });
            // The native close event is queued after the open attribute is removed.
            await page.waitForFunction(id => document.querySelector(`#${id} input[type=password]`).value === "", identifier);
            await page.locator("#fixture-overlay").evaluate(element => element.remove());
            assert.equal(await opener.evaluate(element => element === document.activeElement), true);
            assert.equal(await dialog.locator("input[type=password]").inputValue(), "");
            await opener.click();
            await dialog.locator("[data-dialog-close]").click();
            await dialog.waitFor({ state: "hidden" });
            assert.equal(await opener.evaluate(element => element === document.activeElement), true);
        }
        // Delegation must survive enhanced navigation replacing the page contents.
        await page.evaluate(html => { document.body.innerHTML = html; }, markup);
        await page.locator("[data-dialog-open='destination-dialog']").click();
        assert.equal(await page.locator("#destination-dialog").evaluate(element => element.matches(":modal")), true);
        await page.close();
    }
    console.log("Mailbox dialogs: desktop/mobile top-layer visibility, constrained scrolling, focus, Escape, Close, password clearing, and enhanced-navigation replacement passed.");
} finally {
    await browser.close();
}
