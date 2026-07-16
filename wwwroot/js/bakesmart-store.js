(function () {
  const cache = new Map();
  let posSessionsCache = [];
  let activeSessionCache = null;
  let refreshAllPromise = null;
  let refreshAllCompletedAt = 0;
  const refreshAllTtlMs = 15000;
  const persistentCacheTtlMs = 5 * 60 * 1000;

  async function request(url, options = {}) {
    const method = String(options.method || "GET").toUpperCase();
    const shouldTimeout = method === "GET";
    const controller = shouldTimeout ? new AbortController() : null;
    const timeout = controller ? window.setTimeout(() => controller.abort(), 8000) : null;

    let response;
    try {
      response = await fetch(url, {
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {})
      },
      signal: controller?.signal,
      ...options
      });
    } catch (error) {
      if (error?.name === "AbortError") {
        throw new Error("La consulta tardo demasiado. Mostrando datos disponibles.");
      }
      throw error;
    } finally {
      if (timeout) window.clearTimeout(timeout);
    }

    if (!response.ok) {
      const text = await response.text();
      let message = text || `Error ${response.status}`;
      try {
        const payload = JSON.parse(text);
        message = payload.message || payload.title || message;
      } catch { }
      throw new Error(message);
    }

    return response.status === 204 ? null : response.json();
  }

  function persistentKey(key) {
    return `bakesmart.store.${key}`;
  }

  function readPersistent(key) {
    try {
      const raw = sessionStorage.getItem(persistentKey(key));
      if (!raw) return null;
      const entry = JSON.parse(raw);
      if (!entry || Date.now() - Number(entry.time || 0) > persistentCacheTtlMs) return null;
      return entry.data;
    } catch {
      return null;
    }
  }

  function writePersistent(key, data) {
    try {
      sessionStorage.setItem(persistentKey(key), JSON.stringify({ time: Date.now(), data }));
    } catch { }
  }

  function publish(key, data) {
    cache.set(key, data);
    writePersistent(key, data);
    window.dispatchEvent(new CustomEvent("bakesmart:data-ready", { detail: { key } }));
    return data;
  }

  async function load(key, url, fallback = [], options = {}) {
    const force = Boolean(options.force);
    const cachedData = cache.has(key) ? cache.get(key) : readPersistent(key);

    if (!force && cachedData != null) {
      cache.set(key, cachedData);
      request(url)
        .then(data => publish(key, data))
        .catch(() => { });
      return cachedData;
    }

    const data = await request(url);
    return publish(key, data);
  }

  function cached(key, fallback = []) {
    return cache.has(key) ? cache.get(key) : fallback;
  }

  async function loadPosSessions(options = {}) {
    const force = Boolean(options.force);
    const cachedSessions = readPersistent("posSessions");
    if (!force && cachedSessions) {
      posSessionsCache = cachedSessions;
      activeSessionCache = posSessionsCache.find(s => normalizeStatus(s.status).startsWith("abiert")) || null;
      request("/api/pos/sessions")
        .then(data => {
          posSessionsCache = publish("posSessions", data);
          activeSessionCache = posSessionsCache.find(s => normalizeStatus(s.status).startsWith("abiert")) || null;
        })
        .catch(() => { });
      return posSessionsCache;
    }

    try {
      posSessionsCache = publish("posSessions", await request("/api/pos/sessions"));
      activeSessionCache = posSessionsCache.find(s => normalizeStatus(s.status).startsWith("abiert")) || null;
    } catch {
      posSessionsCache = [];
      activeSessionCache = null;
    }
    return posSessionsCache;
  }

  function normalizeStatus(value) {
    return String(value || "")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .trim()
      .toLowerCase();
  }

  function activePosSession() {
    return activeSessionCache;
  }

  const loaders = {
    orders: options => load("orders", "/api/orders", [], options),
    inventory: options => load("inventory", "/api/inventory", [], options),
    inventoryMovements: options => load("inventoryMovements", "/api/inventory/movements", [], options),
    customers: options => load("customers", "/api/customers", [], options),
    promotions: options => load("promotions", "/api/promotions", [], options),
    users: options => load("users", "/api/users", [], options),
    roles: options => load("roles", "/api/roles", [], options),
    posConfig: options => load("posConfig", "/api/pos/config", {}, options),
    accounting: options => load("accounting", "/api/accounting", {}, options),
    logs: options => load("logs", "/api/logs", [], options)
  };

  function refreshKeysForCurrentPage() {
    const page = String(document.body?.dataset?.page || location.pathname || "").toLowerCase();
    const keys = new Set(["orders", "inventory", "posConfig"]);

    if (page.startsWith("/pos")) keys.add("customers");
    if (page.startsWith("/client")) keys.add("customers");
    if (page.startsWith("/orders")) keys.add("customers");
    if (page.startsWith("/marketing")) ["customers", "promotions"].forEach(key => keys.add(key));
    if (page.startsWith("/inventory")) keys.add("inventoryMovements");
    if (page.startsWith("/users")) ["users", "roles"].forEach(key => keys.add(key));
    if (page.startsWith("/roles")) keys.add("roles");
    if (page.startsWith("/accounting")) keys.add("accounting");
    if (page.startsWith("/audit")) keys.add("logs");
    if (page.startsWith("/reports")) ["customers", "inventoryMovements", "accounting"].forEach(key => keys.add(key));
    if (page.startsWith("/admin")) ["customers", "promotions", "users", "roles", "accounting"].forEach(key => keys.add(key));

    return [...keys];
  }

  function refreshAll(options = {}) {
    const force = options === true || Boolean(options.force);
    const now = Date.now();
    const keys = Array.isArray(options.keys) ? options.keys : refreshKeysForCurrentPage();
    if (!force && refreshAllPromise) return refreshAllPromise;
    if (!force && keys.every(key => cache.has(key)) && now - refreshAllCompletedAt < refreshAllTtlMs) {
      return Promise.resolve([]);
    }

    const missingOrForcedKeys = force ? keys : keys.filter(key => !cache.has(key) || now - refreshAllCompletedAt >= refreshAllTtlMs);
    refreshAllPromise = Promise.allSettled(missingOrForcedKeys.map(key => {
      const loader = loaders[key];
      return loader ? loader({ force }) : null;
    }).filter(Boolean)).finally(() => {
      refreshAllCompletedAt = Date.now();
      refreshAllPromise = null;
    });

    return refreshAllPromise;
  }

  function exportCsv(fileName, rows) {
    const headers = Object.keys(rows[0] || {});
    const csv = [
      headers.join(","),
      ...rows.map(row => headers.map(header => `"${String(row[header] ?? "").replaceAll('"', '""')}"`).join(","))
    ].join("\n");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8;" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  }

  const api = {
    refresh: refreshAll,
    refreshClient() {
      return Promise.allSettled([
        load("orders", "/api/orders"),
        load("inventory", "/api/inventory"),
        load("posConfig", "/api/pos/config", {})
      ]);
    },
    async refreshPos() {
      await loadPosSessions();
      window.dispatchEvent(new CustomEvent("bakesmart:data-ready", { detail: { key: "posSessions" } }));
    },

    async createOrder(input) {
      return request("/api/orders", { method: "POST", body: JSON.stringify(input) });
    },

    async loadDefaultAddress() {
      return request("/api/addresses/default");
    },

    async loadAddresses() {
      return request("/api/addresses");
    },

    async loadProfile() {
      return request("/api/profile/current");
    },

    async saveSetting(key, value) {
      const all = {};
      all[key] = value;
      return request("/api/settings", { method: "POST", body: JSON.stringify(all) });
    },

    async saveAllSettings(settings) {
      return request("/api/settings", { method: "POST", body: JSON.stringify(settings) });
    },

    async loadSettings() {
      return request("/api/settings");
    },

    async loadCatalogOptions() {
      return request("/api/catalog/options");
    },

    async uploadSiteImage(file) {
      const formData = new FormData();
      formData.append("file", file);
      const response = await fetch("/api/assets/site-images", {
        method: "POST",
        body: formData
      });

      if (!response.ok) {
        const text = await response.text();
        let message = text || `Error ${response.status}`;
        try {
          const payload = JSON.parse(text);
          message = payload.message || payload.title || message;
        } catch { }
        throw new Error(message);
      }

      return response.json();
    },

    orders: {
      list() {
        const trackingSteps = ["Pendiente pago", "Confirmado", "En produccion", "Listo", "En camino", "Entregado"];
        const stepFor = status => {
          const normalized = String(status || "").normalize("NFD").replace(/[\u0300-\u036f]/g, "");
          const index = trackingSteps.findIndex(step => step === normalized);
          return index >= 0 ? index : 0;
        };

        return cached("orders").map(order => ({
          ...order,
          customerName: order.customerName || order.cliente,
          status: order.status || order.estado,
          channel: order.channel || order.canal,
          deliveryDate: order.deliveryDate || order.entrega,
          createdAt: order.createdAt || order.entrega || new Date().toISOString(),
          address: order.address || "",
          productId: Number(order.productId || 0),
          quantity: Number(order.quantity || 1),
          unitPrice: Number(order.unitPrice || 0),
          items: order.items || [{
            productId: Number(order.productId || 0),
            name: order.producto || "Pedido",
            quantity: Number(order.quantity || 1),
            unitPrice: Number(order.unitPrice || 0)
          }],
          tracking: order.tracking || {
            destinationLat: Number(order.destinationLat || order.destinationLatitude || 0),
            destinationLng: Number(order.destinationLng || order.destinationLongitude || 0),
            currentStep: stepFor(order.status || order.estado),
            steps: trackingSteps
          }
        }));
      },
      byClient(email) {
        const value = String(email || "").toLowerCase();
        return api.orders.list().filter(order => String(order.customerEmail || order.email || "").toLowerCase() === value || !value);
      },
      async updateStatus(id, status) {
        await request(`/api/orders/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });
        return load("orders", "/api/orders", [], { force: true });
      },
      async markPaid(id, method = "Efectivo") {
        await request(`/api/orders/${id}/pay`, { method: "POST", body: JSON.stringify({ method }) });
        return load("orders", "/api/orders", [], { force: true });
      },
      async delete(id) {
        await request(`/api/orders/${id}`, { method: "DELETE" });
        return load("orders", "/api/orders", [], { force: true });
      },
      create() {
        throw new Error("Crear pedidos debe hacerse desde el formulario del sistema.");
      },
      simulateTracking() {
        return null;
      }
    },
    inventory: {
      list() {
        return cached("inventory").map(product => ({
          ...product,
          code: product.code || product.sku,
          description: product.description || product.item,
          unit: product.unit || product.unidad,
          minStock: product.minStock ?? product.min,
          productType: product.productType || product.type || ""
        }));
      },
      sellable() {
        return api.inventory.list()
          .filter(product => product.active && Number(product.stock) > 0)
          .filter(product => String(product.productType || product.type || "").toLowerCase() === "producto terminado");
      },
      history() { return cached("inventoryMovements"); },
      add() { throw new Error("Crear productos debe hacerse desde el formulario del sistema."); },
      update() { throw new Error("Editar productos debe hacerse desde el formulario del sistema."); },
      move() { throw new Error("Registrar movimientos debe hacerse desde el formulario del sistema."); },
      toggle() { throw new Error("Cambiar estado debe hacerse desde el formulario del sistema."); }
    },
    customers: {
      list() { return cached("customers"); },
      search(query) {
        const q = String(query || "").toLowerCase();
        return cached("customers").filter(customer =>
          !q ||
          String(customer.fullName || "").toLowerCase().includes(q) ||
          String(customer.email || "").toLowerCase().includes(q) ||
          String(customer.phone || "").includes(q)
        );
      },
      async addFrequent(id) {
        await request(`/api/customers/${id}/frequent`, { method: "POST", body: JSON.stringify({}) });
        return load("customers", "/api/customers", [], { force: true });
      }
    },
    marketing: {
      promotions() { return cached("promotions"); },
      async addPromotion(input = {}) {
        const result = await request("/api/promotions", {
          method: "POST",
          body: JSON.stringify({
            id: input.id ? Number(input.id) : null,
            name: input.name || "",
            startDate: input.startDate,
            endDate: input.endDate,
            discount: Number(input.discount || 0),
            isActive: input.isActive !== false
          })
        });
        await load("promotions", "/api/promotions", [], { force: true });
        return result;
      },
      async togglePromotion(id) {
        await request(`/api/promotions/${id}/toggle`, { method: "POST", body: JSON.stringify({}) });
        return load("promotions", "/api/promotions", [], { force: true });
      },
      async sendCampaign(input = {}) {
        return request("/api/marketing/campaigns", {
          method: "POST",
          body: JSON.stringify({
            subject: input.subject || "",
            message: input.message || "",
            customerIds: input.customerIds || []
          })
        });
      }
    },
    users: {
      list() { return cached("users"); },
      async save(input = {}) {
        const payload = {
          id: input.id ? Number(input.id) : null,
          firstName: input.firstName || "",
          lastName: input.lastName || "",
          email: input.email || "",
          phone: input.phone || "",
          address: input.address || "",
          role: input.role || "Cliente",
          password: input.password || ""
        };

        const result = await request("/api/users", { method: "POST", body: JSON.stringify(payload) });
        await load("users", "/api/users", [], { force: true });
        return result;
      },
      async toggle(id) {
        await request(`/api/users/${id}/toggle`, { method: "POST", body: JSON.stringify({}) });
        const rows = await load("users", "/api/users", [], { force: true });
        return rows.find(user => Number(user.id) === Number(id));
      }
    },
    roles: {
      list() { return cached("roles"); }
    },
    pos: {
      config() { return cached("posConfig", { iva: 0, frequentCustomerDiscount: 0, paymentMethods: [] }); },
      activeSession() { return activePosSession(); },
      cachedSessions() { return posSessionsCache; },
      async sessions() {
        await loadPosSessions();
        return posSessionsCache;
      },
      recentSales() {
        return request("/api/pos/sales");
      },
      searchProducts(query) {
        const q = String(query || "").toLowerCase();
        return api.inventory.sellable()
          .filter(product =>
            !q ||
            String(product.code || product.sku || "").toLowerCase().includes(q) ||
            String(product.description || product.item || "").toLowerCase().includes(q)
          )
          .map(product => ({
            id: product.id,
            code: product.code || product.sku,
            description: product.description || product.item,
            name: product.description || product.item,
            price: product.price,
            stock: product.stock
          }));
      },
      async openSession(amount = 0) {
        const result = await request("/api/pos/open", { method: "POST", body: JSON.stringify({ amount: Number(amount) }) });
        await loadPosSessions({ force: true });
        return result;
      },
      async closeSession(id, declared = 0) {
        const result = await request("/api/pos/close", { method: "POST", body: JSON.stringify({ id: Number(id), declaredAmount: Number(declared) }) });
        await loadPosSessions({ force: true });
        return result;
      },
      async savePaymentMethod(input = {}) {
        const result = await request("/api/pos/payment-methods", {
          method: "POST",
          body: JSON.stringify({
            id: input.id ? Number(input.id) : null,
            name: input.name || "",
            commissionRate: Number(input.commissionRate || 0),
            isActive: input.isActive !== false,
            account: input.account || ""
          })
        });
        await load("posConfig", "/api/pos/config", {}, { force: true });
        return result;
      },
      async togglePaymentMethod(id) {
        await request(`/api/pos/payment-methods/${id}/toggle`, { method: "POST", body: JSON.stringify({}) });
        return load("posConfig", "/api/pos/config", {}, { force: true });
      },
      async creditNote(input = {}) {
        return request("/api/pos/credit-notes", {
          method: "POST",
          body: JSON.stringify({
            saleId: Number(input.saleId || 0),
            reason: input.reason || ""
          })
        });
      },
      async sell(input = {}) {
        await loadPosSessions({ force: true });
        const session = activePosSession();
        if (!session) throw new Error("Debe abrir caja antes de confirmar ventas.");

        const items = Array.isArray(input.items) ? input.items : [];
        if (!items.length) throw new Error("Agregue productos al carrito antes de cobrar.");

        const products = api.inventory.list();
        const subtotal = items.reduce((sum, item) => {
          const product = products.find(row => Number(row.id) === Number(item.productId));
          return sum + Number(product?.price || 0) * Number(item.quantity || 0);
        }, 0);
        const customer = api.customers.list().find(row =>
          (input.customerEmail && String(row.email || "").toLowerCase() === String(input.customerEmail).toLowerCase()) ||
          (input.customerName && String(row.fullName || "").toLowerCase() === String(input.customerName).toLowerCase())
        );
        const manualDiscountRate = Math.min(Math.max(Number(input.discountRate || 0), 0), 1);
        const frequentDiscountRate = customer?.frequent ? Number(api.pos.config().frequentCustomerDiscount || 0) : 0;
        const activePromotionRate = Number(api.pos.config().activePromotionDiscount || 0);
        const discountRate = Math.max(manualDiscountRate, frequentDiscountRate, activePromotionRate);
        const taxRate = Number(api.pos.config().iva || 0);
        const discountedSubtotal = Math.max(0, subtotal - subtotal * discountRate);
        const tax = discountedSubtotal * taxRate;
        const total = discountedSubtotal + tax;

        const saleInput = {
          customerName: input.customerName || "Cliente de mostrador",
          customerEmail: input.customerEmail || null,
          customerPhone: input.customerPhone || null,
          paymentMethod: input.paymentMethod || "Efectivo",
          subtotal,
          discount: subtotal * discountRate,
          tax,
          total,
          notes: null,
          items: items.map(item => ({
            productId: item.productId,
            quantity: item.quantity,
            unitPrice: products.find(row => Number(row.id) === Number(item.productId))?.price || 0
          }))
        };

        const result = await request("/api/pos/sell", { method: "POST", body: JSON.stringify(saleInput) });
        await loadPosSessions();
        await load("inventory", "/api/inventory", [], { force: true });
        await load("inventoryMovements", "/api/inventory/movements", [], { force: true });
        return result;
      }
    },
    accounting: {
      overview() { return cached("accounting", { entries: [], expensesCount: 0, supplierPaymentsCount: 0 }); },
      entries() { return api.accounting.overview().entries || []; },
      expenses() { return Array.from({ length: Number(api.accounting.overview().expensesCount || 0) }); },
      supplierPayments() { return Array.from({ length: Number(api.accounting.overview().supplierPaymentsCount || 0) }); },
      async refresh() { return load("accounting", "/api/accounting", {}, { force: true }); },
      async addExpense(input = {}) {
        const result = await request("/api/accounting/expenses", {
          method: "POST",
          body: JSON.stringify({
            description: input.description || "",
            amount: Number(input.amount || 0),
            account: input.account || ""
          })
        });
        await api.accounting.refresh();
        return result;
      },
      async addSupplierPayment(input = {}) {
        const result = await request("/api/accounting/supplier-payments", {
          method: "POST",
          body: JSON.stringify({
            supplier: input.supplier || "",
            amount: Number(input.amount || 0),
            account: input.account || "",
            method: input.method || ""
          })
        });
        await api.accounting.refresh();
        return result;
      },
      async reconcile() {
        return request("/api/accounting/reconcile-pos", { method: "POST", body: JSON.stringify({}) });
      },
      async dailyClose(type = "DIARIO") {
        return request("/api/accounting/daily-close", { method: "POST", body: JSON.stringify({ type }) });
      }
    },
    reports: {
      async load(type, start = "", end = "") {
        const params = new URLSearchParams();
        if (start) params.set("start", start);
        if (end) params.set("end", end);
        return request(`/api/reports/${type}${params.toString() ? `?${params}` : ""}`);
      },
      sales() { return { rows: [], totalIncome: 0, totalTransactions: 0 }; },
      inventory() { return { rows: cached("inventory"), lowStock: cached("inventory").filter(x => Number(x.stock) <= Number(x.min)).length, negativeStock: 0 }; },
      users() { return { rows: cached("users"), activeUsers: cached("users").filter(x => x.active).length }; },
      promotions() { return { rows: cached("promotions"), activePromotions: cached("promotions").filter(x => x.active).length }; },
      cashClosures() { return { rows: [], totalSales: 0 }; },
      orders() { return { rows: cached("orders"), totalOrders: cached("orders").length }; },
      exportCsv
    },
    logs() {
      return cached("logs");
    },
    geo: {
      origin() {
        const config = cached("posConfig", {});
        const defaultLat = 9.9281;
        const defaultLng = -84.0907;
        const lat = Number(config.originLatitude);
        const lng = Number(config.originLongitude);
        return {
          name: config.originName || "BakeSmart Patri",
          address: config.originAddress || "",
          city: "San Jose",
          country: "Costa Rica",
          lat: Number.isFinite(lat) ? lat : defaultLat,
          lng: Number.isFinite(lng) ? lng : defaultLng
        };
      },
      presets() {
        return [];
      },
      resolveDestination(address, preset = {}) {
        const origin = api.geo.origin();
        const lat = Number(preset.lat || preset.latitude);
        const lng = Number(preset.lng || preset.longitude);
        return {
          lat: Number.isFinite(lat) ? lat : origin.lat,
          lng: Number.isFinite(lng) ? lng : origin.lng,
          name: address || preset.name || "Destino",
          label: address || preset.name || "Destino",
          country: preset.country || "Costa Rica"
        };
      }
    }
  };

  window.BakeSmartStore = api;
})();
