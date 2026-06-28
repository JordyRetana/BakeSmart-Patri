(function () {
  const cache = new Map();
  let posSessionsCache = [];
  let activeSessionCache = null;

  async function request(url, options = {}) {
    const response = await fetch(url, {
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {})
      },
      ...options
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

    return response.status === 204 ? null : response.json();
  }

  async function load(key, url, fallback = []) {
    const data = await request(url);
    cache.set(key, data);
    window.dispatchEvent(new CustomEvent("bakesmart:data-ready", { detail: { key } }));
    return data;
  }

  function cached(key, fallback = []) {
    return cache.has(key) ? cache.get(key) : fallback;
  }

  async function loadPosSessions() {
    try {
      posSessionsCache = await request("/api/pos/sessions");
      activeSessionCache = posSessionsCache.find(s => s.status === "Abierta") || null;
    } catch {
      posSessionsCache = [];
      activeSessionCache = null;
    }
    return posSessionsCache;
  }

  function activePosSession() {
    return activeSessionCache;
  }

  function refreshAll() {
    return Promise.allSettled([
      load("orders", "/api/orders"),
      load("inventory", "/api/inventory"),
      load("inventoryMovements", "/api/inventory/movements"),
      load("customers", "/api/customers"),
      load("promotions", "/api/promotions"),
      load("users", "/api/users"),
      load("roles", "/api/roles"),
      load("posConfig", "/api/pos/config", {}),
      load("logs", "/api/logs")
    ]);
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

    orders: {
      list() {
        return cached("orders").map(order => ({
          ...order,
          customerName: order.customerName || order.cliente,
          status: order.status || order.estado,
          channel: order.channel || order.canal,
          deliveryDate: order.deliveryDate || order.entrega,
          createdAt: order.createdAt || order.entrega || new Date().toISOString(),
          address: order.address || "",
          items: order.items || [{ name: order.producto || "Pedido", quantity: 1 }]
        }));
      },
      byClient(email) {
        const value = String(email || "").toLowerCase();
        return api.orders.list().filter(order => String(order.customerEmail || order.email || "").toLowerCase() === value || !value);
      },
      async updateStatus(id, status) {
        await request(`/api/orders/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });
        return load("orders", "/api/orders");
      },
      async markPaid(id, method = "Efectivo") {
        await request(`/api/orders/${id}/pay`, { method: "POST", body: JSON.stringify({ method }) });
        return load("orders", "/api/orders");
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
          minStock: product.minStock ?? product.min
        }));
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
      addFrequent() { throw new Error("La marca de cliente frecuente debe actualizarse desde el sistema."); }
    },
    marketing: {
      promotions() { return cached("promotions"); },
      addPromotion() { throw new Error("Crear promociones debe hacerse desde el formulario del sistema."); }
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
        await load("users", "/api/users");
        return result;
      },
      async toggle(id) {
        await request(`/api/users/${id}/toggle`, { method: "POST", body: JSON.stringify({}) });
        const rows = await load("users", "/api/users");
        return rows.find(user => Number(user.id) === Number(id));
      }
    },
    roles: {
      list() { return cached("roles"); }
    },
    pos: {
      config() { return cached("posConfig", { iva: 0, frequentCustomerDiscount: 0, paymentMethods: [] }); },
      activeSession() { return activePosSession(); },
      async sessions() {
        await loadPosSessions();
        return posSessionsCache;
      },
      searchProducts(query) {
        const q = String(query || "").toLowerCase();
        return cached("inventory")
          .filter(product => product.active && Number(product.stock) > 0)
          .filter(product =>
            !q ||
            String(product.sku || product.code || "").toLowerCase().includes(q) ||
            String(product.item || product.description || "").toLowerCase().includes(q)
          )
          .map(product => ({
            id: product.id,
            code: product.sku,
            description: product.item,
            name: product.item,
            price: product.price,
            stock: product.stock
          }));
      },
      async openSession(amount = 0) {
        const result = await request("/api/pos/open", { method: "POST", body: JSON.stringify({ amount: Number(amount) }) });
        await loadPosSessions();
        return result;
      },
      async closeSession(id, declared = 0) {
        const result = await request("/api/pos/close", { method: "POST", body: JSON.stringify({ id: Number(id), declaredAmount: Number(declared) }) });
        await loadPosSessions();
        return result;
      },
      async sell(input = {}) {
        const session = activePosSession();
        if (!session) throw new Error("Debe abrir caja antes de confirmar ventas.");

        const items = Array.isArray(input.items) ? input.items : [];
        if (!items.length) throw new Error("Agregue productos al carrito antes de cobrar.");

        const products = api.inventory.list();
        const subtotal = items.reduce((sum, item) => {
          const product = products.find(row => Number(row.id) === Number(item.productId));
          return sum + Number(product?.price || 0) * Number(item.quantity || 0);
        }, 0);
        const discountRate = Math.min(Math.max(Number(input.discountRate || 0), 0), 1);
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
        return result;
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
        return {
          name: config.originName || "BakeSmart Patri",
          address: config.originAddress || "",
          lat: Number(config.originLatitude),
          lng: Number(config.originLongitude)
        };
      },
      presets() {
        return [];
      },
      resolveDestination(address, preset = {}) {
        const origin = api.geo.origin();
        return {
          lat: Number(preset.lat || preset.latitude || origin.lat),
          lng: Number(preset.lng || preset.longitude || origin.lng),
          label: address || preset.name || "Destino"
        };
      }
    }
  };

  window.BakeSmartStore = api;
})();
