// Orbit website: navigation, scroll effects, live numbers from GitHub and the animated orbit in the hero.
(() => {
  const REPO = "ph1l59/Orbit";
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---------- Navigation: turns into a floating capsule after scrolling ----------
  const nav = document.querySelector(".nav");
  const onScroll = () => nav && nav.classList.toggle("scrolled", window.scrollY > 24);
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  const toggle = document.querySelector(".menu-toggle");
  const links = document.querySelector(".nav-links");
  if (toggle && links) {
    toggle.addEventListener("click", () => links.classList.toggle("open"));
    links.querySelectorAll("a").forEach(a => a.addEventListener("click", () => links.classList.remove("open")));
  }

  // ---------- Fade sections in while scrolling ----------
  const io = new IntersectionObserver(entries => {
    for (const e of entries) if (e.isIntersecting) { e.target.classList.add("visible"); io.unobserve(e.target); }
  }, { threshold: 0.12, rootMargin: "0px 0px -40px 0px" });
  document.querySelectorAll(".reveal").forEach(el => io.observe(el));

  // ---------- App window in the hero straightens up while scrolling ----------
  const showWindow = document.querySelector(".showcase .window");
  if (showWindow && !reduced) {
    const tilt = () => {
      const rect = showWindow.getBoundingClientRect();
      const progress = Math.min(1, Math.max(0, 1 - (rect.top - window.innerHeight * 0.25) / (window.innerHeight * 0.75)));
      showWindow.style.setProperty("--tilt", (18 * (1 - progress)).toFixed(2) + "deg");
      showWindow.style.setProperty("--scale", (0.94 + 0.06 * progress).toFixed(3));
    };
    window.addEventListener("scroll", tilt, { passive: true });
    tilt();
  }

  // ---------- Version, downloads and releases from GitHub ----------
  const fmt = n => n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1).replace(/\.0$/, "") + "k" : String(n);
  fetch(`https://api.github.com/repos/${REPO}/releases?per_page=100`)
    .then(r => r.ok ? r.json() : Promise.reject(r.status))
    .then(releases => {
      if (!Array.isArray(releases) || releases.length === 0) return;
      const published = releases.filter(r => !r.draft);
      const latest = published.find(r => !r.prerelease) || published[0];
      if (latest) document.querySelectorAll("[data-version]").forEach(el => el.textContent = latest.tag_name.replace(/^v/i, "v"));
      let downloads = 0;
      for (const r of published) for (const a of r.assets || []) if (/\.exe$/i.test(a.name)) downloads += a.download_count;
      // Downloads are shown once they look impressive; until then the box says "Windows 10 & 11".
      if (downloads >= 1000)
        document.querySelectorAll("[data-downloads-stat]").forEach(el => el.innerHTML = `<div class="num">${fmt(downloads)}</div><div class="label">Downloads</div>`);
      document.querySelectorAll("[data-releases]").forEach(el => el.textContent = published.length);
      window.dispatchEvent(new CustomEvent("orbit-releases", { detail: published }));
    })
    .catch(() => window.dispatchEvent(new CustomEvent("orbit-releases", { detail: null })));

  // ---------- Animated orbit in the hero ----------
  const canvas = document.getElementById("space");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  let w = 0, h = 0, dpr = 1, stars = [], mouse = { x: 0, y: 0 }, smooth = { x: 0, y: 0 };

  const rings = [
    { r: 0.30, speed: 0.11, moons: 2, tilt: 0.30 },
    { r: 0.44, speed: 0.075, moons: 3, tilt: 0.30 },
    { r: 0.60, speed: 0.05, moons: 2, tilt: 0.30 },
    { r: 0.78, speed: 0.032, moons: 3, tilt: 0.30 },
  ];
  rings.forEach((ring, i) => ring.offsets = Array.from({ length: ring.moons }, (_, k) => k / ring.moons * Math.PI * 2 + i * 0.9));

  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    w = canvas.clientWidth; h = canvas.clientHeight;
    canvas.width = w * dpr; canvas.height = h * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const count = Math.round(Math.min(320, w * h / 5200));
    stars = Array.from({ length: count }, () => ({
      x: Math.random() * w, y: Math.random() * h,
      r: Math.random() * 1.3 + 0.2,
      depth: Math.random() * 0.8 + 0.2,
      phase: Math.random() * Math.PI * 2,
      speed: 0.6 + Math.random() * 1.6,
    }));
  }

  window.addEventListener("resize", resize);
  window.addEventListener("mousemove", e => {
    mouse.x = (e.clientX / window.innerWidth - 0.5) * 2;
    mouse.y = (e.clientY / window.innerHeight - 0.5) * 2;
  }, { passive: true });
  resize();

  let shooting = null, nextShot = 3;

  function frame(ms) {
    const t = ms / 1000;
    smooth.x += (mouse.x - smooth.x) * 0.04;
    smooth.y += (mouse.y - smooth.y) * 0.04;
    ctx.clearRect(0, 0, w, h);

    // Stars with a little parallax and twinkle
    for (const s of stars) {
      const x = s.x - smooth.x * 14 * s.depth, y = s.y - smooth.y * 10 * s.depth;
      const a = 0.35 + 0.65 * (0.5 + 0.5 * Math.sin(t * s.speed + s.phase)) * s.depth;
      ctx.globalAlpha = a;
      ctx.fillStyle = s.depth > 0.75 ? "#e9d5ff" : "#ffffff";
      ctx.beginPath(); ctx.arc(x, y, s.r, 0, Math.PI * 2); ctx.fill();
    }
    ctx.globalAlpha = 1;

    // Orbits around a point a bit below the headline
    const cx = w / 2 + smooth.x * 18, cy = h * 0.62 + smooth.y * 10;
    const base = Math.min(w * 0.62, 760);
    const tiltBase = 0.26 + smooth.y * 0.03;

    // soft glow in the middle
    const glow = ctx.createRadialGradient(cx, cy, 0, cx, cy, base * 0.9);
    glow.addColorStop(0, "rgba(124,58,237,0.22)");
    glow.addColorStop(1, "rgba(124,58,237,0)");
    ctx.fillStyle = glow;
    ctx.fillRect(0, 0, w, h);

    rings.forEach((ring, i) => {
      const rx = base * ring.r * (w < 700 ? 1.25 : 1), ry = rx * (tiltBase + i * 0.012);
      const rot = -0.12 + smooth.x * 0.04;
      ctx.save();
      ctx.translate(cx, cy);
      ctx.rotate(rot);
      const grad = ctx.createLinearGradient(-rx, 0, rx, 0);
      grad.addColorStop(0, "rgba(168,85,247,0.05)");
      grad.addColorStop(0.5, "rgba(192,132,252,0.32)");
      grad.addColorStop(1, "rgba(232,121,249,0.08)");
      ctx.strokeStyle = grad;
      ctx.lineWidth = 1.2;
      ctx.beginPath(); ctx.ellipse(0, 0, rx, ry, 0, 0, Math.PI * 2); ctx.stroke();

      // moons with a short trail
      for (const off of ring.offsets) {
        const a = off + t * ring.speed * (reduced ? 0 : 1) * (i % 2 ? -1 : 1);
        for (let k = 10; k >= 0; k--) {
          const ak = a - k * 0.035 * (i % 2 ? -1 : 1);
          const mx = Math.cos(ak) * rx, my = Math.sin(ak) * ry;
          const front = Math.sin(ak) > 0;
          ctx.globalAlpha = (k === 0 ? 1 : (1 - k / 11) * 0.35) * (front ? 1 : 0.45);
          ctx.fillStyle = k === 0 ? "#f5e8ff" : "#c084fc";
          ctx.beginPath(); ctx.arc(mx, my, k === 0 ? 2.6 : 1.6, 0, Math.PI * 2); ctx.fill();
          if (k === 0) {
            ctx.globalAlpha = front ? 0.5 : 0.2;
            const g = ctx.createRadialGradient(mx, my, 0, mx, my, 14);
            g.addColorStop(0, "rgba(192,132,252,0.9)"); g.addColorStop(1, "rgba(192,132,252,0)");
            ctx.fillStyle = g; ctx.beginPath(); ctx.arc(mx, my, 14, 0, Math.PI * 2); ctx.fill();
          }
        }
        ctx.globalAlpha = 1;
      }
      ctx.restore();
    });

    // An occasional shooting star
    if (!reduced) {
      if (!shooting && t > nextShot) {
        shooting = { x: Math.random() * w * 0.7 + w * 0.15, y: Math.random() * h * 0.3, start: t };
        nextShot = t + 5 + Math.random() * 6;
      }
      if (shooting) {
        const p = (t - shooting.start) / 0.9;
        if (p >= 1) shooting = null;
        else {
          const x = shooting.x + p * 260, y = shooting.y + p * 120;
          const g = ctx.createLinearGradient(x - 120, y - 55, x, y);
          g.addColorStop(0, "rgba(255,255,255,0)"); g.addColorStop(1, `rgba(255,255,255,${0.8 * (1 - p)})`);
          ctx.strokeStyle = g; ctx.lineWidth = 1.4;
          ctx.beginPath(); ctx.moveTo(x - 120, y - 55); ctx.lineTo(x, y); ctx.stroke();
        }
      }
    }

    if (!reduced) requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
})();
