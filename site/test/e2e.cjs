// The published site in Chromium, served with the production headers from vercel.json.
// Usage: node site/test/e2e.cjs <published wwwroot folder>
const { chromium, devices } = require("playwright");
const { spawn } = require("child_process");
const path = require("path");

const root = path.resolve(process.argv[2] || "out/wwwroot");
const repo = path.resolve(__dirname, "../..");
const port = 4300 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${port}/`;
let failures = 0;
const check = (ok, name, detail = "") => {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${!ok && detail ? "  " + detail : ""}`);
  if (!ok) failures++;
};
const squash = s => (s || "").replace(/\s+/g, " ").trim();

(async () => {
  const server = spawn("python3", [path.join(__dirname, "serve.py"), process.env.VERCEL_JSON || path.join(repo, "vercel.json"), root, String(port)], { stdio: "inherit" });
  try {
    for (let i = 0; i < 50; i++) {
      try { if ((await fetch(base)).ok) break; } catch { /* starting */ }
      await new Promise(r => setTimeout(r, 100));
    }
    const head = await fetch(base);
    const csp = head.headers.get("content-security-policy") || "";
    check((csp.match(/script-src [^;]*/) || [""])[0] === "script-src 'self' 'wasm-unsafe-eval'" && csp.includes("connect-src 'self'") && csp.includes("frame-ancestors 'none'"), "strict Content-Security-Policy");
    check((await fetch(base + "data/list.json")).headers.get("cache-control") === "no-cache", "the list is revalidated, not cached for good");

    const browser = await chromium.launch();
    const open = async (options = {}) => {
      const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...options });
      const p = await ctx.newPage();
      const problems = [];
      p.on("pageerror", e => problems.push("error: " + e.message));
      p.on("dialog", d => { problems.push("dialog: " + d.message()); d.dismiss(); });
      p.on("console", m => { if (m.type() === "error") problems.push("console: " + m.text().slice(0, 200)); });
      p.on("request", r => { if (!r.url().startsWith(base)) problems.push("request: " + r.url()); });
      await p.addInitScript(() => { window.__csp = []; document.addEventListener("securitypolicyviolation", e => window.__csp.push(e.violatedDirective + " " + e.blockedURI)); });
      const t0 = Date.now();
      await p.goto(base);
      await p.waitForFunction(() => /listed parties/.test(document.querySelector(".inputs .hint")?.textContent || ""), null, { timeout: 90000 });
      const ms = Date.now() - t0;
      const done = async () => [...problems, ...(await p.evaluate(() => window.__csp)).map(v => "csp: " + v)];
      return { ctx, p, done, ms };
    };
    const screen = async (p, name) => {
      await p.fill("#name", name);
      await p.waitForTimeout(400);
      return squash(await p.textContent(".results"));
    };

    {
      const { ctx, p, done, ms } = await open();
      const loaded = squash(await p.textContent(".inputs .hint"));
      check(/[0-9,]{6} listed parties/.test(loaded), "the list loads", loaded);
      console.log(`     loaded and indexed in ${ms} ms`);
      check(ms < 20000, "it loads within 20 seconds", `${ms} ms`);
      let text = await screen(p, "Banko Nasional de Cuba");
      check(text.includes("1 possible match") && text.includes("BANCO NACIONAL DE CUBA") && text.includes("BANKO ≈ BANCO"), "a misspelt listed bank is found, with reasons", text.slice(0, 200));
      text = await screen(p, "Sokha Chan");
      check(text.includes("No possible matches"), "an ordinary name has no matches", text.slice(0, 120));
      await p.selectOption("select", "vessel");
      text = await screen(p, "Banco Nacional de Cuba");
      check(text.includes("No possible matches"), "the kind filter excludes other kinds");
      await p.selectOption("select", "");
      for (const hostile of ["<img src=x onerror=alert(1)>", "\u202EBanco Nacional de Cuba\u0000", "A".repeat(300), "😀".repeat(100)]) {
        await screen(p, hostile);
        const injected = await p.evaluate(() => document.querySelectorAll(".results img, .results script").length);
        check(injected === 0 && (await p.locator(".results").count()) === 1, "hostile name stays text: " + JSON.stringify(hostile.slice(0, 20)));
      }
      text = await screen(p, "\u202EBanco Nacional de Cuba");
      check(text.includes("BANCO NACIONAL DE CUBA"), "an invisible character doesn't hide a listed name");
      const issues = await done();
      check(issues.length === 0, "no errors, policy violations or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open({ ...devices["iPhone 13"] });
      await screen(p, "Mohammed Ali");
      const [scroll, width] = await p.evaluate(() => [document.documentElement.scrollWidth, innerWidth]);
      check(scroll <= width, "no sideways scrolling on a phone", `page ${scroll}px, screen ${width}px`);
      const spill = await p.evaluate(() => {
        const card = document.querySelector(".results").getBoundingClientRect();
        return [...document.querySelectorAll(".results *")].filter(e => e.getBoundingClientRect().right > card.right + 1).length;
      });
      check(spill === 0, "nothing spills out of the results on a phone", `${spill} elements`);
      check((await done()).length === 0, "phone: no errors");
      await ctx.close();
    }

    {
      const ctx = await browser.newContext();
      const p = await ctx.newPage();
      await p.setContent(`<iframe src="${base}" width="400" height="300"></iframe>`);
      await p.waitForTimeout(1500);
      const frame = p.frames().find(f => f !== p.mainFrame());
      const framed = frame ? await frame.evaluate(() => !!document.querySelector(".brand")).catch(() => false) : false;
      check(!framed, "the page can't be framed");
      await ctx.close();
    }
    await browser.close();
  } finally {
    server.kill();
  }
  console.log(failures ? `\n${failures} failed` : "\nall passed");
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
