(function () {
  "use strict";

  const instances = new WeakMap();
  const DEFAULT_PAGE_SIZE = 8;

  function normalize(value) {
    return String(value || "")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLowerCase()
      .trim();
  }

  function icon(name) {
    return `<i class="fas ${name}" aria-hidden="true"></i>`;
  }

  class SmartTable {
    constructor(table) {
      this.table = table;
      this.body = table.tBodies[0];
      this.query = "";
      this.page = 1;
      this.pageSize = Number(table.dataset.pageSize) || DEFAULT_PAGE_SIZE;
      this.pendingRender = false;
      this.mount();
      this.observe();
      this.render();
    }

    mount() {
      this.table.dataset.smartTableReady = "true";
      this.table.classList.add("smart-table");

      const scrollHost = this.table.closest(
        ".table-container, .table-responsive, .dashboard-table-wrap, .audit-table-wrap, [class*='table-wrap']"
      );
      this.host = scrollHost || this.table;

      this.toolbar = document.createElement("div");
      this.toolbar.className = "smart-table-toolbar";
      this.toolbar.innerHTML = `
        <label class="smart-table-search">
          <button type="button" class="smart-table-search-button" aria-label="Buscar">${icon("fa-magnifying-glass")}</button>
          <input type="search" placeholder="Buscar en esta tabla" aria-label="Buscar en esta tabla" autocomplete="off" />
          <button type="button" class="smart-table-clear" aria-label="Limpiar busqueda" hidden>${icon("fa-xmark")}</button>
        </label>
        <div class="smart-table-toolbar-meta">
          <span class="smart-table-match"><strong>0</strong> resultados</span>
        </div>`;

      this.dock = document.createElement("div");
      this.dock.className = "smart-table-dock";
      this.dock.innerHTML = `
        <div class="smart-table-page-size">
          <span>Mostrar</span>
          <select aria-label="Filas por pagina">
            <option value="5">5</option>
            <option value="8">8</option>
            <option value="12">12</option>
            <option value="20">20</option>
          </select>
        </div>
        <nav class="smart-table-pagination" aria-label="Paginacion de tabla"></nav>
        <div class="smart-table-range" aria-live="polite"></div>`;

      this.host.insertAdjacentElement("beforebegin", this.toolbar);
      this.host.insertAdjacentElement("afterend", this.dock);

      this.searchInput = this.toolbar.querySelector("input");
      this.searchButton = this.toolbar.querySelector(".smart-table-search-button");
      this.clearButton = this.toolbar.querySelector(".smart-table-clear");
      this.matchLabel = this.toolbar.querySelector(".smart-table-match strong");
      this.pageSizeSelect = this.dock.querySelector("select");
      this.pagination = this.dock.querySelector(".smart-table-pagination");
      this.range = this.dock.querySelector(".smart-table-range");
      this.pageSizeSelect.value = String(this.pageSize);

      this.searchInput.addEventListener("input", () => {
        this.query = normalize(this.searchInput.value);
        this.page = 1;
        this.clearButton.hidden = !this.query;
        this.render();
      });

      this.searchButton.addEventListener("click", () => this.searchInput.focus());

      this.clearButton.addEventListener("click", () => {
        this.searchInput.value = "";
        this.query = "";
        this.page = 1;
        this.clearButton.hidden = true;
        this.searchInput.focus();
        this.render();
      });

      this.pageSizeSelect.addEventListener("change", () => {
        this.pageSize = Number(this.pageSizeSelect.value) || DEFAULT_PAGE_SIZE;
        this.page = 1;
        this.render();
      });

      this.pagination.addEventListener("click", (event) => {
        const button = event.target.closest("button[data-page]");
        if (!button || button.disabled) return;
        this.page = Number(button.dataset.page);
        this.render();
        this.toolbar.scrollIntoView({ behavior: "smooth", block: "nearest" });
      });
    }

    observe() {
      this.observer = new MutationObserver(() => this.scheduleRender());
      this.observer.observe(this.body, { childList: true, subtree: true, characterData: true });
    }

    scheduleRender() {
      if (this.pendingRender) return;
      this.pendingRender = true;
      requestAnimationFrame(() => {
        this.pendingRender = false;
        this.render();
      });
    }

    getRows() {
      return Array.from(this.body.rows).filter((row) => !row.dataset.smartIgnore);
    }

    isMessageRow(row) {
      return row.cells.length === 1 && row.cells[0].colSpan > 1;
    }

    pageItems(totalPages) {
      if (totalPages <= 5) return Array.from({ length: totalPages }, (_, index) => index + 1);
      const items = [1];
      const start = Math.max(2, this.page - 1);
      const end = Math.min(totalPages - 1, this.page + 1);
      if (start > 2) items.push("ellipsis-start");
      for (let value = start; value <= end; value += 1) items.push(value);
      if (end < totalPages - 1) items.push("ellipsis-end");
      items.push(totalPages);
      return items;
    }

    renderPagination(totalPages) {
      const previous = Math.max(1, this.page - 1);
      const next = Math.min(totalPages, this.page + 1);
      const pages = this.pageItems(totalPages)
        .map((item) => {
          if (typeof item === "string") return `<span class="smart-table-ellipsis">...</span>`;
          const current = item === this.page;
          return `<button type="button" data-page="${item}" class="smart-table-page${current ? " is-current" : ""}" ${current ? 'aria-current="page"' : ""}>${item}</button>`;
        })
        .join("");

      this.pagination.innerHTML = `
        <button type="button" data-page="${previous}" class="smart-table-step" aria-label="Pagina anterior" ${this.page === 1 ? "disabled" : ""}>${icon("fa-arrow-left")}</button>
        <div class="smart-table-pages">${pages}</div>
        <button type="button" data-page="${next}" class="smart-table-step" aria-label="Pagina siguiente" ${this.page === totalPages ? "disabled" : ""}>${icon("fa-arrow-right")}</button>`;
    }

    render() {
      const rows = this.getRows();
      const messageRows = rows.filter((row) => this.isMessageRow(row));
      const dataRows = rows.filter((row) => !this.isMessageRow(row));
      const matches = dataRows.filter((row) => !this.query || normalize(row.innerText).includes(this.query));
      const totalPages = Math.max(1, Math.ceil(matches.length / this.pageSize));
      this.page = Math.min(Math.max(1, this.page), totalPages);
      const start = (this.page - 1) * this.pageSize;
      const end = Math.min(start + this.pageSize, matches.length);
      const visibleRows = new Set(matches.slice(start, end));

      rows.forEach((row) => row.classList.toggle("smart-row-hidden", !visibleRows.has(row)));
      messageRows.forEach((row) => row.classList.toggle("smart-row-hidden", dataRows.length > 0));

      this.matchLabel.textContent = String(matches.length);
      this.range.textContent = matches.length ? `${start + 1}-${end} de ${matches.length}` : "Sin resultados";
      this.renderPagination(totalPages);

      const shouldShowDock = matches.length > this.pageSize;
      this.dock.classList.toggle("is-compact", !shouldShowDock);
      this.pagination.hidden = !shouldShowDock;

      let noResults = this.body.querySelector("tr[data-smart-empty]");
      if (!matches.length && dataRows.length && this.query) {
        if (!noResults) {
          noResults = document.createElement("tr");
          noResults.dataset.smartEmpty = "true";
          noResults.dataset.smartIgnore = "true";
          noResults.innerHTML = `<td colspan="${Math.max(this.table.rows[0]?.cells.length || 1, 1)}"><div class="smart-table-empty">${icon("fa-magnifying-glass")}<strong>No encontramos coincidencias</strong><span>Prueba con otro termino de busqueda.</span></div></td>`;
          this.body.appendChild(noResults);
        }
        noResults.classList.remove("smart-row-hidden");
      } else if (noResults) {
        noResults.remove();
      }
    }
  }

  function enhance(table) {
    if (!(table instanceof HTMLTableElement)) return;
    if (table.dataset.smartTable === "off" || table.dataset.smartTableReady === "true") return;
    if (!table.tHead || !table.tBodies.length) return;
    instances.set(table, new SmartTable(table));
  }

  function scan(root) {
    if (root instanceof HTMLTableElement) enhance(root);
    root.querySelectorAll?.("table").forEach(enhance);
  }

  function init() {
    scan(document);
    const pageObserver = new MutationObserver((mutations) => {
      mutations.forEach((mutation) => mutation.addedNodes.forEach((node) => {
        if (node instanceof HTMLElement) scan(node);
      }));
    });
    pageObserver.observe(document.body, { childList: true, subtree: true });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
