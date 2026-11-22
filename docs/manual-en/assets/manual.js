/* Spektrafilm Darkroom for Capture One: user manual navigation (English edition).
   Works from file:// without a server: the chapter list lives here, the search index in search-index.js. */
(function () {
  "use strict";

  var MANUAL_VERSION = "release 1.2.4 (22 November 2026): panel NativePanel 5203C9FC, renderer 86F83C1E";

  var CHAPTERS = [
    { file: "index.html", num: "", title: "Start", blurb: "Who the manual is for and how to read it" },
    { file: "01-thinking.html", num: "1", title: "How to think about Spektrafilm", blurb: "Scene, film and paper instead of sliders" },
    { file: "02-quick-start.html", num: "2", title: "Quick start", blurb: "Your first photo in ten steps" },
    { file: "03-capture-one-preparation.html", num: "3", title: "Preparing in Capture One", blurb: "What to keep in Capture One and what to leave to the film" },
    { file: "04-films-and-papers.html", num: "4", title: "Films and papers", blurb: "The character of each profile and proven pairs" },
    { file: "05-exposure-and-contrast.html", num: "5", title: "Exposure and contrast", blurb: "Film and print exposure, gamma, preflash" },
    { file: "06-colour.html", num: "6", title: "Colour", blurb: "Filtration, DIR, adaptation, bases and aging" },
    { file: "07-optics.html", num: "7", title: "Optics", blurb: "Halation, scatter, diffusion filters" },
    { file: "08-grain.html", num: "8", title: "Grain", blurb: "Grain models and matching the output size" },
    { file: "09-finishing.html", num: "9", title: "Finishing", blurb: "Curves, scanner, geometry, paper and border" },
    { file: "10-tools.html", num: "10", title: "Working tools", blurb: "Undo, versions, presets, comparisons" },
    { file: "11-export.html", num: "11", title: "Export", blurb: "Formats, colour spaces, batches, LUTs" },
    { file: "12-recipes.html", num: "12", title: "Recipes", blurb: "Ready-made looks step by step" },
    { file: "13-troubleshooting.html", num: "13", title: "Problems and limits", blurb: "Symptom, cause, solution" },
    { file: "14-glossary.html", num: "14", title: "Glossary", blurb: "Darkroom and technical terms" },
    { file: "15-sources.html", num: "15", title: "Sources and licences", blurb: "Where the knowledge, code and photos come from" },
    { file: "16-running-and-data.html", num: "16", title: "Running and data", blurb: "Shortcut, folders, backups, updates" },
    { file: "17-cheat-sheet.html", num: "17", title: "Cheat sheet", blurb: "The most important controls on one page" }
  ];

  var here = (location.pathname.split("/").pop() || "index.html");
  var current = CHAPTERS.findIndex(function (c) { return c.file === here; });

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (key) {
      if (key === "text") node.textContent = attrs[key];
      else node.setAttribute(key, attrs[key]);
    });
    (children || []).forEach(function (child) { if (child) node.appendChild(child); });
    return node;
  }

  function fold(text) {
    return (text || "").toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/ł/g, "l");
  }

  /* ---------- Theme ---------- */

  function storedTheme() {
    try { return localStorage.getItem("spektrafilm-manual-theme"); } catch (e) { return null; }
  }
  function applyTheme(theme) {
    if (theme === "light" || theme === "dark") document.documentElement.setAttribute("data-theme", theme);
    else document.documentElement.removeAttribute("data-theme");
  }
  applyTheme(storedTheme());

  function effectiveTheme() {
    var set = document.documentElement.getAttribute("data-theme");
    if (set) return set;
    return window.matchMedia && matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  /* ---------- Sidebar ---------- */

  function buildSidebar() {
    var sidebar = document.querySelector(".sidebar");
    if (!sidebar) return;
    sidebar.textContent = "";
    var brand = el("a", { class: "brand", href: "index.html" }, [
      el("strong", { text: "Spektrafilm Darkroom" }),
      el("span", { text: "User manual" })
    ]);
    var toggle = el("button", { class: "nav-toggle", type: "button", "aria-expanded": "false", text: "Chapters and search" });
    toggle.addEventListener("click", function () {
      var open = sidebar.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });

    var input = el("input", { type: "search", id: "manual-search", placeholder: "Search for a control or term", "aria-label": "Search the manual", autocomplete: "off" });
    var results = el("ul", { class: "search-results", id: "manual-search-results", "aria-live": "polite" });
    var search = el("div", { class: "search", role: "search" }, [input, results]);
    wireSearch(input, results);

    var list = el("ol", { class: "chapters" });
    CHAPTERS.forEach(function (chapter, index) {
      var link = el("a", { href: chapter.file }, [
        el("span", { class: "num", text: chapter.num }),
        el("span", { text: chapter.title })
      ]);
      if (index === current) link.setAttribute("aria-current", "page");
      list.appendChild(el("li", {}, [link]));
    });

    var theme = el("button", { class: "theme-switch", type: "button" });
    function labelTheme() {
      theme.textContent = effectiveTheme() === "dark" ? "Light theme (paper)" : "Dark theme (darkroom)";
    }
    labelTheme();
    theme.addEventListener("click", function () {
      var next = effectiveTheme() === "dark" ? "light" : "dark";
      applyTheme(next);
      try { localStorage.setItem("spektrafilm-manual-theme", next); } catch (e) { /* storage unavailable */ }
      labelTheme();
    });

    var body = el("div", { class: "nav-body" }, [search, el("nav", { "aria-label": "Chapters" }, [list]), theme]);
    sidebar.appendChild(brand);
    sidebar.appendChild(toggle);
    sidebar.appendChild(body);
  }

  /* ---------- Search ---------- */

  function wireSearch(input, results) {
    var index = (window.MANUAL_INDEX || []).map(function (entry) {
      return { entry: entry, text: fold(entry.t + " " + (entry.k || "") + " " + (entry.s || "")) , title: fold(entry.t) };
    });
    function render() {
      var query = fold(input.value.trim());
      results.textContent = "";
      if (query.length < 2) return;
      var words = query.split(/\s+/);
      var hits = index.filter(function (item) {
        return words.every(function (w) { return item.text.indexOf(w) >= 0; });
      }).sort(function (a, b) {
        return (b.title.indexOf(words[0]) === 0) - (a.title.indexOf(words[0]) === 0);
      }).slice(0, 25);
      if (!hits.length) {
        results.appendChild(el("li", {}, [el("a", { href: "13-troubleshooting.html", text: "No results. See the chapter on problems or the glossary." })]));
        return;
      }
      hits.forEach(function (item) {
        results.appendChild(el("li", {}, [el("a", { href: item.entry.u }, [
          document.createTextNode(item.entry.t),
          el("small", { text: item.entry.s || "" })
        ])]));
      });
    }
    input.addEventListener("input", render);
    input.addEventListener("keydown", function (event) {
      if (event.key === "Escape") { input.value = ""; render(); }
      if (event.key === "Enter") {
        var first = results.querySelector("a");
        if (first) location.href = first.getAttribute("href");
      }
    });
  }

  /* ---------- Test strip ---------- */

  function buildStrip() {
    var strip = document.querySelector(".test-strip");
    if (!strip) return;
    strip.textContent = "";
    var steps = CHAPTERS.slice(1);
    var list = el("ol", { "aria-label": "Chapters as a test strip" });
    steps.forEach(function (chapter, i) {
      var density = Math.round(6 + (i / (steps.length - 1)) * 88);
      var link = el("a", { href: chapter.file, title: chapter.num + ". " + chapter.title, style: "--density:" + density + "%" }, [
        el("span", { class: "visually-hidden", text: "Chapter " + chapter.num + ": " + chapter.title })
      ]);
      if (CHAPTERS[current] === chapter) link.setAttribute("aria-current", "page");
      list.appendChild(el("li", {}, [link]));
    });
    strip.appendChild(list);
    var position = current > 0 ? "Chapter " + CHAPTERS[current].num + " of " + steps.length : "Test strip: each step is a chapter";
    strip.appendChild(el("div", { class: "caption" }, [
      el("span", { text: position }),
      el("span", { text: current > 0 ? CHAPTERS[current].title : "from light to dark, from basics to details" })
    ]));
  }

  /* ---------- Page table of contents ---------- */

  function slug(text) {
    return fold(text).replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 60);
  }

  function buildToc() {
    var aside = document.querySelector(".page-toc");
    var main = document.querySelector("main");
    if (!aside || !main) return;
    var heads = Array.prototype.slice.call(main.querySelectorAll("h2, .control > h3"));
    heads.forEach(function (h) { if (!h.id) h.id = slug(h.textContent); });
    if (heads.length < 2) { aside.remove(); return; }
    var list = el("ol");
    var links = heads.map(function (h) {
      var a = el("a", { href: "#" + h.id, text: h.textContent });
      list.appendChild(el("li", { class: h.tagName === "H2" ? "" : "sub" }, [a]));
      return a;
    });
    aside.textContent = "";
    aside.appendChild(el("h2", { text: "On this page" }));
    aside.appendChild(list);
    if ("IntersectionObserver" in window) {
      var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          links.forEach(function (a) { a.classList.toggle("active", a.getAttribute("href") === "#" + entry.target.id); });
        });
      }, { rootMargin: "0px 0px -75% 0px" });
      heads.forEach(function (h) { observer.observe(h); });
    }
  }

  /* ---------- Pager and colophon ---------- */

  function buildPager() {
    var pager = document.querySelector(".pager");
    if (!pager || current < 0) return;
    pager.textContent = "";
    var prev = CHAPTERS[current - 1];
    var next = CHAPTERS[current + 1];
    if (prev) pager.appendChild(el("a", { class: "prev", href: prev.file, rel: "prev" }, [el("small", { text: "Previous" }), el("span", { text: (prev.num ? prev.num + ". " : "") + prev.title })]));
    if (next) pager.appendChild(el("a", { class: "next", href: next.file, rel: "next" }, [el("small", { text: "Next" }), el("span", { text: next.num + ". " + next.title })]));
    var colophon = document.querySelector(".colophon");
    if (colophon && !colophon.textContent.trim()) colophon.textContent = "Describes " + MANUAL_VERSION + ".";
  }

  /* ---------- Figures ---------- */

  function wireFigures() {
    Array.prototype.forEach.call(document.querySelectorAll("img[data-local]"), function (img) {
      img.addEventListener("error", function () {
        var box = el("div", { class: "missing", text: "Illustration missing: " + img.getAttribute("src") });
        img.replaceWith(box);
      });
    });
    Array.prototype.forEach.call(document.querySelectorAll(".compare"), function (figure) {
      var range = figure.querySelector("input[type=range]");
      if (!range) return;
      function update() { figure.style.setProperty("--split", range.value + "%"); }
      range.addEventListener("input", update);
      update();
    });
  }

  function buildMap() {
    var map = document.querySelector(".chapter-map");
    if (!map) return;
    map.textContent = "";
    CHAPTERS.slice(1).forEach(function (chapter) {
      map.appendChild(el("li", {}, [el("a", { href: chapter.file }, [
        el("span", { class: "num", text: chapter.num }),
        el("span", {}, [el("strong", { text: chapter.title }), el("span", { text: chapter.blurb })])
      ])]));
    });
  }

  function init() {
    buildMap();
    buildSidebar();
    buildStrip();
    buildToc();
    buildPager();
    wireFigures();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
