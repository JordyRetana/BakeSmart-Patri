
(function () {
  const STORAGE_KEY = 'bakesmart.store.v2';

  const clone = (obj) => JSON.parse(JSON.stringify(obj));
  const nowIso = () => new Date().toISOString();
  const randId = (prefix='id') => `${prefix}-${Math.random().toString(36).slice(2, 10)}`;
  const crc = (n) => new Intl.NumberFormat('es-CR', {style:'currency', currency:'CRC', minimumFractionDigits:0}).format(Number(n || 0));


  const ORIGIN_HUB = {
    name: 'BakeSmart Patri · Sagrada Familia',
    city: 'Sagrada Familia',
    country: 'Costa Rica',
    lat: 9.9141,
    lng: -84.0739,
    code: 'CR-SF'
  };

  const DESTINATION_PRESETS = [
    { id: 'escuela', name: 'Centro Educativo El Carmelo', city: 'Sagrada Familia', country: 'Costa Rica', lat: 9.9157, lng: -84.0702, keywords: ['escuela', 'colegio', 'centro educativo', 'carmelo'] },
    { id: 'escazu', name: 'Escazú Centro', city: 'Escazú', country: 'Costa Rica', lat: 9.9186, lng: -84.1394, keywords: ['escazu', 'escazú'] },
    { id: 'santa-ana', name: 'Santa Ana Centro', city: 'Santa Ana', country: 'Costa Rica', lat: 9.9326, lng: -84.1827, keywords: ['santa ana'] },
    { id: 'curridabat', name: 'Curridabat', city: 'Curridabat', country: 'Costa Rica', lat: 9.9118, lng: -84.0341, keywords: ['curridabat'] },
    { id: 'cartago', name: 'Cartago Centro', city: 'Cartago', country: 'Costa Rica', lat: 9.8644, lng: -83.9194, keywords: ['cartago'] },
    { id: 'heredia', name: 'Heredia Centro', city: 'Heredia', country: 'Costa Rica', lat: 9.9981, lng: -84.1165, keywords: ['heredia'] },
    { id: 'alajuela', name: 'Alajuela Centro', city: 'Alajuela', country: 'Costa Rica', lat: 10.0163, lng: -84.2116, keywords: ['alajuela'] },
    { id: 'panama-city', name: 'Ciudad de Panamá', city: 'Ciudad de Panamá', country: 'Panamá', lat: 8.9943, lng: -79.5188, keywords: ['panama', 'panamá', 'panama city', 'ciudad de panamá', 'ciudad de panama'] }
  ];

  function resolveDestination(address, fallback = {}) {
    const source = String(address || '').trim().toLowerCase();
    const matched = DESTINATION_PRESETS.find(item => item.keywords.some(keyword => source.includes(keyword)));
    if (matched) {
      return {
        name: matched.name,
        city: matched.city,
        country: matched.country,
        lat: matched.lat,
        lng: matched.lng,
        routeMode: matched.country === 'Costa Rica' ? 'ground' : 'air',
        isInternational: matched.country !== 'Costa Rica'
      };
    }

    const fallbackLat = Number(fallback.lat);
    const fallbackLng = Number(fallback.lng);
    const hasFallbackCoords = Number.isFinite(fallbackLat) && Number.isFinite(fallbackLng);
    return {
      name: String(address || 'Destino del cliente').trim() || 'Destino del cliente',
      city: hasFallbackCoords ? 'Destino personalizado' : 'San José',
      country: source.includes('panam') ? 'Panamá' : 'Costa Rica',
      lat: hasFallbackCoords ? fallbackLat : 9.9180,
      lng: hasFallbackCoords ? fallbackLng : -84.1390,
      routeMode: source.includes('panam') ? 'air' : 'ground',
      isInternational: source.includes('panam')
    };
  }

  const seed = {
    version: 3,
    config: {
      iva: 0.13,
      frequentCustomerDiscount: 0.05,
      paymentMethods: [
        { id: 'cash', name: 'Efectivo', active: true, commission: 0, account: 'Caja General' },
        { id: 'sinpe', name: 'SINPE Móvil', active: true, commission: 0, account: 'Banco / SINPE' },
        { id: 'card', name: 'Tarjeta', active: true, commission: 0.025, account: 'Banco / Tarjetas' }
      ]
    },
    roles: [
      { name: 'Admin', description: 'Acceso completo', permissions: ['usuarios', 'roles', 'inventario', 'pedidos', 'reportes', 'contabilidad', 'marketing', 'pos', 'produccion'] },
      { name: 'Staff', description: 'Operación diaria', permissions: ['inventario', 'pedidos', 'reportes', 'pos', 'produccion'] },
      { name: 'Cliente', description: 'Portal del cliente', permissions: ['perfil', 'mis-pedidos', 'seguimiento'] }
    ],
    users: [
      { id: 1, firstName: 'Admin', lastName: 'Demo', email: 'admin@demo.com', phone: '88881111', password: '1234', address: 'San José, Costa Rica', role: 'Admin', active: true, createdAt: '2026-02-01T09:00:00Z' },
      { id: 2, firstName: 'Staff', lastName: 'Demo', email: 'staff@demo.com', phone: '88882222', password: '1234', address: 'Heredia, Costa Rica', role: 'Staff', active: true, createdAt: '2026-02-02T09:00:00Z' },
      { id: 3, firstName: 'Cliente', lastName: 'Demo', email: 'cliente@demo.com', phone: '88883333', password: '1234', address: 'Escazú, San José', role: 'Cliente', active: true, createdAt: '2026-02-03T09:00:00Z' }
    ],
    customers: [
      { id: 201, fullName: 'Cliente Demo', email: 'cliente@demo.com', phone: '88883333', address: 'Escazú, San José', frequent: true, totalSpent: 96000 },
      { id: 202, fullName: 'María Gómez', email: 'maria@correo.com', phone: '88884444', address: 'Curridabat, San José', frequent: true, totalSpent: 164000 },
      { id: 203, fullName: 'Ana Solís', email: 'ana@correo.com', phone: '88885555', address: 'Cartago Centro', frequent: false, totalSpent: 45000 },
      { id: 204, fullName: 'Clínica Santa Ana', email: 'compras@clinicasantana.com', phone: '22225555', address: 'Santa Ana, San José', frequent: true, totalSpent: 228000 }
    ],
    products: [
      { id: 301, code: 'PAST-001', description: 'Cake Red Velvet 1.5kg', category: 'Pasteles', subcategory: 'Personalizado', price: 32000, stock: 10, active: true, createdAt: '2026-02-01T10:00:00Z' },
      { id: 302, code: 'PAST-002', description: 'Cheesecake Frutos Rojos', category: 'Postres', subcategory: 'Cheesecake', price: 28000, stock: 8, active: true, createdAt: '2026-02-01T10:10:00Z' },
      { id: 303, code: 'CUP-003', description: 'Cupcakes Caja 12', category: 'Cupcakes', subcategory: 'Caja', price: 18000, stock: 20, active: true, createdAt: '2026-02-01T10:20:00Z' },
      { id: 304, code: 'GAL-004', description: 'Galletas Decoradas', category: 'Galletas', subcategory: 'Eventos', price: 12000, stock: 25, active: true, createdAt: '2026-02-01T10:30:00Z' },
      { id: 305, code: 'BROW-005', description: 'Brownie Gourmet', category: 'Postres', subcategory: 'Brownie', price: 8500, stock: 0, active: true, createdAt: '2026-02-01T10:40:00Z' }
    ],
    inventoryMovements: [],
    orders: [
      {
        id: 1012, customerId: 201, customerName: 'Cliente Demo', customerEmail: 'cliente@demo.com', channel: 'Web',
        items: [{ productId: 301, code:'PAST-001', name: 'Cake Red Velvet 1.5kg', quantity: 1, unitPrice: 32000 }],
        address: 'Escazú, San José', notes: 'Entregar antes de las 4 pm', status: 'En producción',
        paymentStatus: 'Pagado', paymentMethod: 'Tarjeta', subtotal: 32000, discount: 1600, tax: 3952, total: 34352,
        createdAt: '2026-02-20T15:30:00Z', deliveryDate: '2026-03-27', tracking: {
          currentLat: 9.9235, currentLng: -84.1437, destinationLat: 9.9180, destinationLng: -84.1390,
          steps: ['Pedido aceptado', 'En producción', 'Listo', 'En camino', 'Entregado'], currentStep: 1
        }
      },
      {
        id: 1014, customerId: 202, customerName: 'María Gómez', customerEmail: 'maria@correo.com', channel: 'WhatsApp',
        items: [{ productId: 302, code:'PAST-002', name: 'Cheesecake Frutos Rojos', quantity: 1, unitPrice: 28000 }],
        address: 'Curridabat, San José', notes: '', status: 'Pendiente pago',
        paymentStatus: 'Pendiente', paymentMethod: 'SINPE', subtotal: 28000, discount: 0, tax: 3640, total: 31640,
        createdAt: '2026-02-22T18:00:00Z', deliveryDate: '2026-03-28', tracking: {
          currentLat: 9.935, currentLng: -84.06, destinationLat: 9.915, destinationLng: -84.03,
          steps: ['Pedido aceptado', 'En producción', 'Listo', 'En camino', 'Entregado'], currentStep: 0
        }
      },
      {
        id: 1018, customerId: 204, customerName: 'Clínica Santa Ana', customerEmail: 'compras@clinicasantana.com', channel: 'Tienda',
        items: [{ productId: 303, code:'CUP-003', name: 'Cupcakes Caja 12', quantity: 2, unitPrice: 18000 }],
        address: 'Santa Ana, San José', notes: 'Evento corporativo', status: 'Confirmado',
        paymentStatus: 'Pagado', paymentMethod: 'Transferencia', subtotal: 36000, discount: 0, tax: 4680, total: 40680,
        createdAt: '2026-02-25T09:00:00Z', deliveryDate: '2026-03-29', tracking: {
          currentLat: 9.94, currentLng: -84.2, destinationLat: 9.93, destinationLng: -84.18,
          steps: ['Pedido aceptado', 'En producción', 'Listo', 'En camino', 'Entregado'], currentStep: 2
        }
      }
    ],
    promotions: [
      { id: 401, name: 'Cliente frecuente', startDate: '2026-03-01', endDate: '2026-12-31', discount: 0.05, active: true },
      { id: 402, name: 'Semana dulce', startDate: '2026-03-20', endDate: '2026-03-31', discount: 0.10, active: true }
    ],
    sales: [],
    cashSessions: [],
    accountingEntries: [],
    expenseEntries: [],
    supplierPayments: [],
    reportAudit: [],
    logs: []
  };

  function read() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return initialize();
      const parsed = JSON.parse(raw);
      if (!parsed.version || parsed.version < seed.version) return migrate(parsed);
      return parsed;
    } catch (e) {
      return initialize();
    }
  }

  function write(state) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    return state;
  }

  function initialize() {
    const state = clone(seed);
    bootstrap(state);
    write(state);
    return state;
  }

  function bootstrap(state) {
    if (!state.inventoryMovements.length) {
      state.products.forEach((product) => {
        state.inventoryMovements.push({
          id: randId('mov'),
          productId: product.id,
          code: product.code,
          type: 'CREACION',
          quantity: product.stock,
          responsible: 'Sistema',
          createdAt: product.createdAt || nowIso(),
          note: 'Carga inicial'
        });
      });
    }
    if (!state.sales.length) {
      state.orders.filter(o => o.paymentStatus === 'Pagado').forEach(o => {
        state.sales.push({
          id: `sale-${o.id}`,
          orderId: o.id,
          customerName: o.customerName,
          subtotal: o.subtotal,
          tax: o.tax,
          total: o.total,
          paymentMethod: o.paymentMethod,
          createdAt: o.createdAt
        });
      });
    }
    if (!state.accountingEntries.length) {
      state.sales.forEach((sale, idx) => {
        state.accountingEntries.push({
          id: idx + 1,
          type: 'VENTA',
          referenceId: sale.orderId,
          account: 'Ingresos por ventas',
          debit: sale.total,
          credit: sale.total,
          balanced: true,
          createdAt: sale.createdAt,
          note: `Asiento automático de la venta #${sale.orderId}`
        });
      });
    }
    state.orders = state.orders.map((order) => {
      const destination = resolveDestination(order.address, { lat: order?.tracking?.destinationLat, lng: order?.tracking?.destinationLng });
      order.tracking = Object.assign({
        currentLat: ORIGIN_HUB.lat,
        currentLng: ORIGIN_HUB.lng,
        destinationLat: destination.lat,
        destinationLng: destination.lng,
        routeMode: destination.routeMode,
        destinationCountry: destination.country,
        destinationLabel: destination.name,
        originLabel: ORIGIN_HUB.name
      }, order.tracking || {});

      if (!Number.isFinite(Number(order.tracking.currentLat))) order.tracking.currentLat = ORIGIN_HUB.lat;
      if (!Number.isFinite(Number(order.tracking.currentLng))) order.tracking.currentLng = ORIGIN_HUB.lng;
      order.tracking.destinationLat = Number.isFinite(Number(order.tracking.destinationLat)) ? Number(order.tracking.destinationLat) : destination.lat;
      order.tracking.destinationLng = Number.isFinite(Number(order.tracking.destinationLng)) ? Number(order.tracking.destinationLng) : destination.lng;
      order.tracking.routeMode = order.tracking.routeMode || destination.routeMode;
      order.tracking.destinationCountry = order.tracking.destinationCountry || destination.country;
      order.tracking.destinationLabel = order.tracking.destinationLabel || destination.name;
      order.tracking.originLabel = order.tracking.originLabel || ORIGIN_HUB.name;
      return order;
    });

    if (!state.logs.length) {
      [
        ['LOGIN', 'Inicio de sesión demo'],
        ['CREACION_PRODUCTO', 'Carga inicial de inventario'],
        ['CREAR_PEDIDO', 'Carga inicial de pedidos'],
        ['GENERAR_REPORTE', 'Carga inicial de reportes']
      ].forEach(([type, detail]) => addLog(state, type, detail, 'Sistema'));
    }
  }

  function migrate(parsed) {
    const merged = Object.assign({}, clone(seed), parsed || {});
    bootstrap(merged);
    write(merged);
    return merged;
  }

  function addLog(state, type, detail, responsible = 'Sistema', extra = {}) {
    state.logs.push({
      id: randId('log'),
      type,
      detail,
      responsible,
      createdAt: nowIso(),
      ...extra
    });
  }

  function getState() { return read(); }
  function update(mutator) {
    const state = read();
    mutator(state);
    write(state);
    return clone(state);
  }

  function nextNumericId(collection, start = 1) {
    const max = collection.reduce((m, x) => Math.max(m, Number(x.id) || 0), start - 1);
    return max + 1;
  }

  const api = {
    reset() { initialize(); return clone(read()); },
    exportState() { return clone(read()); },
    importState(data) { write(data); return clone(read()); },
    currentUser() {
      const email = (document.body.dataset.userEmail || '').toLowerCase();
      if (!email) return null;
      return read().users.find(u => u.email.toLowerCase() === email) || null;
    },
    roles: {
      list() { return clone(read().roles); }
    },
    users: {
      list() { return clone(read().users); },
      create(input) {
        const phone = String(input.phone || '').replace(/\D/g, '');
        const email = String(input.email || '').trim().toLowerCase();
        if (!input.firstName || !input.lastName || !email || !input.password || !phone || !input.role) {
          throw new Error('Todos los campos obligatorios del usuario deben completarse.');
        }
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new Error('El correo no tiene un formato válido.');
        if (!/^\d{8,15}$/.test(phone)) throw new Error('El teléfono debe contener solo números válidos.');
        const state = read();
        if (state.users.some(u => u.email.toLowerCase() === email || u.phone === phone)) {
          throw new Error('Ya existe un usuario con ese correo o teléfono.');
        }
        const user = {
          id: nextNumericId(state.users),
          firstName: input.firstName.trim(),
          lastName: input.lastName.trim(),
          email,
          phone,
          password: input.password,
          address: (input.address || '').trim(),
          role: input.role,
          active: true,
          createdAt: nowIso()
        };
        state.users.push(user);
        addLog(state, 'CREAR_USUARIO', `Usuario ${email} creado`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(user);
      },
      update(id, input) {
        const state = read();
        const user = state.users.find(u => Number(u.id) === Number(id));
        if (!user) throw new Error('El usuario no existe.');
        const email = String(input.email || '').trim().toLowerCase();
        const phone = String(input.phone || '').replace(/\D/g, '');
        if (!input.firstName || !input.lastName || !email || !phone || !input.role) throw new Error('Faltan campos obligatorios.');
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new Error('El correo no tiene un formato válido.');
        if (!/^\d{8,15}$/.test(phone)) throw new Error('El teléfono debe contener solo números válidos.');
        if (state.users.some(u => Number(u.id) !== Number(id) && (u.email.toLowerCase() === email || u.phone === phone))) {
          throw new Error('Ya existe otro usuario con ese correo o teléfono.');
        }
        Object.assign(user, {
          firstName: input.firstName.trim(),
          lastName: input.lastName.trim(),
          email, phone,
          address: (input.address || '').trim(),
          role: input.role
        });
        if (input.password) user.password = input.password;
        addLog(state, 'EDITAR_USUARIO', `Usuario ${email} actualizado`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(user);
      },
      toggle(id) {
        return update((state) => {
          const user = state.users.find(u => Number(u.id) === Number(id));
          if (!user) throw new Error('El usuario no existe.');
          user.active = !user.active;
          addLog(state, user.active ? 'ACTIVAR_USUARIO' : 'DESACTIVAR_USUARIO', `Usuario ${user.email} cambió a ${user.active ? 'activo' : 'inactivo'}`, api.currentUser()?.email || 'Sistema');
        });
      }
    },
    customers: {
      list() { return clone(read().customers); },
      search(term='') {
        const q = term.toLowerCase().trim();
        return clone(read().customers.filter(c => !q || c.fullName.toLowerCase().includes(q) || c.email.toLowerCase().includes(q) || c.phone.includes(q)));
      },
      upsertFromOrder(order) {
        const state = read();
        let customer = state.customers.find(c => c.email.toLowerCase() === String(order.customerEmail).toLowerCase());
        if (!customer) {
          customer = {
            id: nextNumericId(state.customers, 200),
            fullName: order.customerName,
            email: order.customerEmail.toLowerCase(),
            phone: order.customerPhone || '00000000',
            address: order.address,
            frequent: false,
            totalSpent: 0
          };
          state.customers.push(customer);
        }
        customer.totalSpent += Number(order.total || 0);
        write(state);
        return clone(customer);
      },
      addFrequent(customerId) {
        return update((state) => {
          const customer = state.customers.find(c => Number(c.id) === Number(customerId));
          if (!customer) throw new Error('Cliente no encontrado.');
          if (customer.frequent) throw new Error('El cliente ya está registrado como frecuente.');
          customer.frequent = true;
          addLog(state, 'CLIENTE_FRECUENTE', `Cliente ${customer.fullName} agregado a frecuentes`, api.currentUser()?.email || 'Sistema');
        });
      }
    },
    inventory: {
      list() { return clone(read().products); },
      history(productId=null) {
        const list = read().inventoryMovements.filter(m => productId ? Number(m.productId) === Number(productId) : true);
        return clone(list.sort((a,b) => new Date(b.createdAt) - new Date(a.createdAt)));
      },
      add(input) {
        const code = String(input.code || '').trim().toUpperCase();
        const description = String(input.description || '').trim();
        const category = String(input.category || '').trim();
        const subcategory = String(input.subcategory || '').trim();
        const price = Number(input.price || 0);
        const stock = Number(input.stock || 0);
        if (!code || !description || !category || price <= 0 || stock < 0) throw new Error('Completa correctamente los campos del producto.');
        const state = read();
        if (state.products.some(p => p.code === code)) throw new Error('El producto ya existe.');
        const product = {
          id: nextNumericId(state.products, 300), code, description, category, subcategory, price, stock,
          active: true, createdAt: nowIso()
        };
        state.products.push(product);
        state.inventoryMovements.push({ id: randId('mov'), productId: product.id, code, type: 'CREACION', quantity: stock, responsible: api.currentUser()?.email || 'Sistema', createdAt: nowIso(), note: 'Producto registrado' });
        addLog(state, 'CREACION_PRODUCTO', `Producto ${code} creado`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(product);
      },
      update(id, input) {
        const state = read();
        const product = state.products.find(p => Number(p.id) === Number(id));
        if (!product) throw new Error('El producto no existe.');
        const code = String(input.code || '').trim().toUpperCase();
        const description = String(input.description || '').trim();
        const category = String(input.category || '').trim();
        const subcategory = String(input.subcategory || '').trim();
        const price = Number(input.price || 0);
        const stock = Number(input.stock || 0);
        if (!code || !description || !category || price <= 0 || stock < 0) throw new Error('Hay valores inválidos en el producto.');
        if (state.products.some(p => Number(p.id) !== Number(id) && p.code === code)) throw new Error('Ya existe otro producto con ese código.');
        const changes = [];
        if (product.stock !== stock) changes.push(`stock ${product.stock}→${stock}`);
        Object.assign(product, { code, description, category, subcategory, price, stock });
        state.inventoryMovements.push({ id: randId('mov'), productId: product.id, code, type: 'ACTUALIZACION', quantity: stock, responsible: api.currentUser()?.email || 'Sistema', createdAt: nowIso(), note: changes.join(', ') || 'Edición general' });
        addLog(state, 'EDICION_PRODUCTO', `Producto ${code} editado`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(product);
      },
      toggle(id) {
        return update((state) => {
          const product = state.products.find(p => Number(p.id) === Number(id));
          if (!product) throw new Error('El producto no existe.');
          const hasMovement = state.inventoryMovements.some(m => Number(m.productId) === Number(id));
          product.active = !product.active;
          addLog(state, 'DESACTIVACION_PRODUCTO', `${product.code} ${product.active ? 'reactivado' : 'desactivado'}${hasMovement ? ' (con historial)' : ''}`, api.currentUser()?.email || 'Sistema');
        });
      },
      move(productId, type, quantity, note='') {
        const qty = Number(quantity || 0);
        if (!['ENTRADA', 'SALIDA'].includes(type)) throw new Error('Tipo de movimiento inválido.');
        if (qty <= 0) throw new Error('La cantidad debe ser mayor que cero.');
        const state = read();
        const product = state.products.find(p => Number(p.id) === Number(productId));
        if (!product) throw new Error('Producto no encontrado.');
        if (type === 'SALIDA' && qty > product.stock) throw new Error('No hay suficiente stock para la salida.');
        product.stock = type === 'ENTRADA' ? product.stock + qty : product.stock - qty;
        state.inventoryMovements.push({ id: randId('mov'), productId: product.id, code: product.code, type, quantity: qty, responsible: api.currentUser()?.email || 'Sistema', createdAt: nowIso(), note });
        addLog(state, type === 'ENTRADA' ? 'ENTRADA_INVENTARIO' : 'SALIDA_INVENTARIO', `${type} ${qty} de ${product.code}`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(product);
      }
    },
    orders: {
      list() { return clone(read().orders.sort((a,b) => Number(b.id) - Number(a.id))); },
      byClient(email) {
        return clone(read().orders.filter(o => String(o.customerEmail).toLowerCase() === String(email).toLowerCase()).sort((a,b) => Number(b.id)-Number(a.id)));
      },
      create(input) {
        const customerName = String(input.customerName || '').trim();
        const customerEmail = String(input.customerEmail || '').trim().toLowerCase();
        const customerPhone = String(input.customerPhone || '').replace(/\D/g, '');
        const address = String(input.address || '').trim();
        const items = Array.isArray(input.items) ? input.items.filter(x => Number(x.quantity) > 0) : [];
        if (!customerName || !customerEmail || !address || !items.length) throw new Error('Completa los datos del pedido y agrega al menos un producto.');
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(customerEmail)) throw new Error('El correo del cliente no es válido.');
        const state = read();
        const normalizedItems = items.map(item => {
          const product = state.products.find(p => Number(p.id) === Number(item.productId));
          if (!product) throw new Error('Uno de los productos ya no existe.');
          const quantity = Number(item.quantity);
          if (quantity <= 0) throw new Error('La cantidad debe ser mayor que cero.');
          if (quantity > product.stock) throw new Error(`No hay stock suficiente para ${product.description}.`);
          product.stock -= quantity;
          state.inventoryMovements.push({ id: randId('mov'), productId: product.id, code: product.code, type: 'SALIDA', quantity, responsible: api.currentUser()?.email || customerEmail, createdAt: nowIso(), note: 'Salida por pedido' });
          return { productId: product.id, code: product.code, name: product.description, quantity, unitPrice: product.price };
        });
        let customer = state.customers.find(c => c.email.toLowerCase() === customerEmail);
        if (!customer) {
          customer = { id: nextNumericId(state.customers, 200), fullName: customerName, email: customerEmail, phone: customerPhone || '00000000', address, frequent: false, totalSpent: 0 };
          state.customers.push(customer);
        }
        const subtotal = normalizedItems.reduce((s, i) => s + (i.quantity * i.unitPrice), 0);
        const promo = customer.frequent ? state.config.frequentCustomerDiscount : 0;
        const manualDiscount = Number(input.discountRate || 0);
        const discountRate = Math.max(promo, manualDiscount);
        const discount = Math.round(subtotal * discountRate);
        const tax = Math.round((subtotal - discount) * state.config.iva);
        const total = subtotal - discount + tax;
        const id = nextNumericId(state.orders, 1000);
        const deliveryDate = input.deliveryDate || new Date(Date.now() + 86400000).toISOString().slice(0,10);
        const destination = resolveDestination(address, { lat: input.destinationLat, lng: input.destinationLng });
        const order = {
          id,
          customerId: customer.id,
          customerName,
          customerEmail,
          customerPhone,
          channel: input.channel || 'Web',
          items: normalizedItems,
          address,
          notes: String(input.notes || '').trim(),
          status: input.paymentStatus === 'Pagado' ? 'Confirmado' : 'Pendiente pago',
          paymentStatus: input.paymentStatus || 'Pendiente',
          paymentMethod: input.paymentMethod || 'Pendiente',
          subtotal, discount, tax, total,
          createdAt: nowIso(),
          deliveryDate,
          tracking: {
            currentLat: ORIGIN_HUB.lat,
            currentLng: ORIGIN_HUB.lng,
            destinationLat: destination.lat,
            destinationLng: destination.lng,
            destinationCountry: destination.country,
            destinationLabel: destination.name,
            originLabel: ORIGIN_HUB.name,
            routeMode: destination.routeMode,
            steps: ['Pedido aceptado', 'En producción', 'Listo', 'En camino', 'Entregado'],
            currentStep: 0
          }
        };
        state.orders.push(order);
        customer.totalSpent += total;
        state.logs.push({ id: randId('log'), type: 'CREAR_PEDIDO', detail: `Pedido #${id} creado`, responsible: api.currentUser()?.email || customerEmail, createdAt: nowIso() });
        if (order.paymentStatus === 'Pagado') {
          state.sales.push({ id: `sale-${id}`, orderId: id, customerName, subtotal, tax, total, paymentMethod: order.paymentMethod, createdAt: order.createdAt });
          const entryId = nextNumericId(state.accountingEntries);
          state.accountingEntries.push({
            id: entryId, type: 'VENTA', referenceId: id, account: 'Ingresos por ventas', debit: total, credit: total, balanced: true, createdAt: nowIso(), note: `Venta #${id}`
          });
          addLog(state, 'SOLICITUD_PEDIDO', `Pedido #${id} pagado y enviado al módulo operativo`, api.currentUser()?.email || customerEmail);
        }
        write(state);
        return clone(order);
      },
      updateStatus(id, status) {
        const state = read();
        const order = state.orders.find(o => Number(o.id) === Number(id));
        if (!order) throw new Error('Pedido no encontrado.');
        const orderStatuses = ['Pendiente pago', 'Confirmado', 'En producción', 'Listo', 'En camino', 'Entregado'];
        if (!orderStatuses.includes(status)) throw new Error('Estado inválido.');
        order.status = status;
        order.tracking.currentStep = Math.max(order.tracking.currentStep, Math.max(0, order.tracking.steps.findIndex(s => s.toLowerCase().includes(status.toLowerCase().split(' ')[0]))));
        if (status === 'En camino') order.tracking.currentStep = 3;
        if (status === 'Entregado') order.tracking.currentStep = 4;
        addLog(state, 'ACTUALIZAR_ESTADO_PEDIDO', `Pedido #${id} cambiado a ${status}`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(order);
      },
      markPaid(id, paymentMethod) {
        const state = read();
        const order = state.orders.find(o => Number(o.id) === Number(id));
        if (!order) throw new Error('Pedido no encontrado.');
        order.paymentStatus = 'Pagado';
        order.paymentMethod = paymentMethod || 'Efectivo';
        order.status = order.status === 'Pendiente pago' ? 'Confirmado' : order.status;
        if (!state.sales.some(s => Number(s.orderId) === Number(id))) {
          state.sales.push({ id: `sale-${id}`, orderId: id, customerName: order.customerName, subtotal: order.subtotal, tax: order.tax, total: order.total, paymentMethod: order.paymentMethod, createdAt: nowIso() });
          state.accountingEntries.push({
            id: nextNumericId(state.accountingEntries), type: 'VENTA', referenceId: id, account: 'Ingresos por ventas', debit: order.total, credit: order.total, balanced: true, createdAt: nowIso(), note: `Venta #${id}`
          });
        }
        addLog(state, 'PAGO_PEDIDO', `Pedido #${id} pagado`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(order);
      },
      simulateTracking(id) {
        const state = read();
        const order = state.orders.find(o => Number(o.id) === Number(id));
        if (!order) throw new Error('Pedido no encontrado.');
        if (order.status === 'En camino') {
          const targetLat = order.tracking.destinationLat;
          const targetLng = order.tracking.destinationLng;
          order.tracking.currentLat = order.tracking.currentLat + ((targetLat - order.tracking.currentLat) * 0.25);
          order.tracking.currentLng = order.tracking.currentLng + ((targetLng - order.tracking.currentLng) * 0.25);
          const near = Math.abs(order.tracking.currentLat - targetLat) < 0.001 && Math.abs(order.tracking.currentLng - targetLng) < 0.001;
          if (near) {
            order.status = 'Entregado';
            order.tracking.currentStep = 4;
            order.tracking.currentLat = targetLat;
            order.tracking.currentLng = targetLng;
            addLog(state, 'ENTREGA_PEDIDO', `Pedido #${id} entregado`, api.currentUser()?.email || 'Sistema');
          }
        }
        write(state);
        return clone(order);
      }
    },
    marketing: {
      promotions() { return clone(read().promotions); },
      addPromotion(input) {
        const name = String(input.name || '').trim();
        const startDate = input.startDate;
        const endDate = input.endDate;
        const discount = Number(input.discount || 0);
        if (!name || !startDate || !endDate || discount <= 0) throw new Error('Completa correctamente la promoción.');
        if (new Date(endDate) < new Date(startDate)) throw new Error('La fecha final no puede ser menor a la fecha inicial.');
        const state = read();
        const item = { id: nextNumericId(state.promotions, 400), name, startDate, endDate, discount, active: true };
        state.promotions.push(item);
        addLog(state, 'PROMOCION_CREADA', `Promoción ${name} creada`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(item);
      }
    },
    pos: {
      config() { return clone(read().config); },
      sessions() { return clone(read().cashSessions.sort((a,b) => new Date(b.openedAt) - new Date(a.openedAt))); },
      activeSession() { return clone(read().cashSessions.find(s => s.status === 'ACTIVO') || null); },
      openSession(openingAmount, userName) {
        const amount = Number(openingAmount || 0);
        if (amount < 0) throw new Error('El monto de apertura no puede ser negativo.');
        const state = read();
        if (state.cashSessions.some(s => s.status === 'ACTIVO')) throw new Error('Ya existe un turno activo.');
        const session = {
          id: nextNumericId(state.cashSessions, 500),
          openedAt: nowIso(),
          closedAt: null,
          openingAmount: amount,
          declaredCash: 0,
          difference: 0,
          userName: userName || api.currentUser()?.email || 'Sistema',
          status: 'ACTIVO',
          totalSales: 0
        };
        state.cashSessions.push(session);
        addLog(state, 'APERTURA_CAJA', `Apertura de caja #${session.id}`, session.userName);
        write(state);
        return clone(session);
      },
      closeSession(id, declaredCash) {
        const state = read();
        const session = state.cashSessions.find(s => Number(s.id) === Number(id));
        if (!session) throw new Error('La caja no existe.');
        session.declaredCash = Number(declaredCash || 0);
        session.closedAt = nowIso();
        session.status = 'CERRADO';
        const cashSales = state.sales.filter(s => s.paymentMethod === 'Efectivo' && new Date(s.createdAt) >= new Date(session.openedAt)).reduce((acc, x) => acc + x.total, 0);
        session.totalSales = state.sales.filter(s => new Date(s.createdAt) >= new Date(session.openedAt)).reduce((acc, x) => acc + x.total, 0);
        session.difference = session.declaredCash - (session.openingAmount + cashSales);
        addLog(state, 'CIERRE_CAJA', `Cierre de caja #${session.id}`, api.currentUser()?.email || 'Sistema');
        write(state);
        return clone(session);
      },
      searchProducts(term='') {
        const q = term.toLowerCase().trim();
        return clone(read().products.filter(p => p.active && p.stock > 0 && (!q || p.code.toLowerCase().includes(q) || p.description.toLowerCase().includes(q))));
      },
      sell(input) {
        const state = read();
        if (!state.cashSessions.some(s => s.status === 'ACTIVO')) throw new Error('Debes abrir un turno antes de vender.');
        const paymentMethod = input.paymentMethod || 'Efectivo';
        const methods = state.config.paymentMethods;
        const method = methods.find(m => m.name === paymentMethod || m.id === paymentMethod.toLowerCase());
        if (!method || !method.active) throw new Error('La forma de pago no está habilitada.');
        const customer = input.customerName || 'Cliente de mostrador';
        const customerEmail = input.customerEmail || 'mostrador@local';
        const order = api.orders.create({
          customerName: customer,
          customerEmail,
          customerPhone: input.customerPhone || '00000000',
          address: input.address || 'Retiro en tienda',
          items: input.items,
          channel: 'POS',
          paymentStatus: 'Pagado',
          paymentMethod: method.name,
          discountRate: Number(input.discountRate || 0)
        });
        const session = state.cashSessions.find(s => s.status === 'ACTIVO');
        if (session) {
          session.totalSales += order.total;
          write(state);
        }
        addLog(state, 'VENTA', `Venta POS del pedido #${order.id}`, api.currentUser()?.email || 'Sistema');
        return order;
      }
    },
    accounting: {
      entries() { return clone(read().accountingEntries.sort((a,b) => new Date(b.createdAt)-new Date(a.createdAt))); },
      expenses() { return clone(read().expenseEntries.sort((a,b) => new Date(b.createdAt)-new Date(a.createdAt))); },
      supplierPayments() { return clone(read().supplierPayments.sort((a,b) => new Date(b.createdAt)-new Date(a.createdAt))); },
      addExpense(input) {
        const amount = Number(input.amount || 0);
        if (amount <= 0 || !input.description || !input.account) throw new Error('Completa correctamente el gasto.');
        const state = read();
        const expense = { id: nextNumericId(state.expenseEntries, 600), description: input.description, amount, account: input.account, createdAt: nowIso(), userName: api.currentUser()?.email || 'Sistema' };
        state.expenseEntries.push(expense);
        state.accountingEntries.push({ id: nextNumericId(state.accountingEntries), type: 'GASTO', referenceId: expense.id, account: input.account, debit: amount, credit: amount, balanced: true, createdAt: nowIso(), note: input.description });
        addLog(state, 'GASTO_CONTABLE', `Gasto ${input.description} registrado`, expense.userName);
        write(state);
        return clone(expense);
      },
      addSupplierPayment(input) {
        const amount = Number(input.amount || 0);
        if (amount <= 0 || !input.supplier || !input.account) throw new Error('Completa correctamente el pago a proveedor.');
        const state = read();
        const payment = { id: nextNumericId(state.supplierPayments, 700), supplier: input.supplier, amount, account: input.account, method: input.method || 'Transferencia', createdAt: nowIso(), userName: api.currentUser()?.email || 'Sistema' };
        state.supplierPayments.push(payment);
        state.accountingEntries.push({ id: nextNumericId(state.accountingEntries), type: 'PAGO_PROVEEDOR', referenceId: payment.id, account: input.account, debit: amount, credit: amount, balanced: true, createdAt: nowIso(), note: `Pago a ${input.supplier}` });
        addLog(state, 'PAGO_PROVEEDOR', `Pago a proveedor ${input.supplier}`, payment.userName);
        write(state);
        return clone(payment);
      },
      dailyClose() {
        const state = read();
        const pending = state.accountingEntries.filter(e => !e.closedAt);
        if (!pending.length) throw new Error('No hay operaciones pendientes para el cierre.');
        const unbalanced = pending.some(e => Number(e.debit) !== Number(e.credit));
        if (unbalanced) throw new Error('Existen asientos descuadrados. SUM(Debe) debe ser igual a SUM(Haber).');
        const closeId = randId('close');
        pending.forEach(e => e.closedAt = nowIso());
        addLog(state, 'CIERRE_CONTABLE', `Cierre contable ${closeId}`, api.currentUser()?.email || 'Sistema');
        write(state);
        return { closeId, count: pending.length, createdAt: nowIso() };
      },
      reconcile() {
        const state = read();
        const reviewed = state.sales.length;
        const issues = state.sales.filter(sale => !state.accountingEntries.some(entry => Number(entry.referenceId) === Number(sale.orderId) && entry.type === 'VENTA')).length;
        addLog(state, 'CONCILIACION', `Conciliación ejecutada: ${reviewed} operaciones revisadas`, api.currentUser()?.email || 'Sistema', { reviewed, issues });
        write(state);
        return { reviewed, issues, status: issues ? 'ERRONEO' : 'CORRECTO', date: nowIso() };
      }
    },
    reports: {
      rangeFilter(items, startDate, endDate, field='createdAt') {
        const start = startDate ? new Date(startDate) : null;
        const end = endDate ? new Date(`${endDate}T23:59:59`) : null;
        if (start && end && end < start) throw new Error('La fecha final no puede ser menor a la fecha inicial.');
        return items.filter(item => {
          const date = new Date(item[field] || item.deliveryDate || item.openedAt || item.startDate);
          return (!start || date >= start) && (!end || date <= end);
        });
      },
      sales(startDate, endDate) {
        const state = read();
        const rows = api.reports.rangeFilter(state.sales, startDate, endDate);
        return {
          rows: clone(rows),
          totalIncome: rows.reduce((a,b) => a + b.total, 0),
          totalTransactions: rows.length
        };
      },
      users() {
        const rows = read().users;
        return { rows: clone(rows), activeUsers: rows.filter(x => x.active).length };
      },
      inventory() {
        const rows = read().products;
        return { rows: clone(rows), lowStock: rows.filter(x => x.stock <= 5).length, negativeStock: rows.filter(x => x.stock < 0).length };
      },
      promotions(startDate, endDate) {
        const rows = api.reports.rangeFilter(read().promotions, startDate, endDate, 'startDate');
        return { rows: clone(rows), activePromotions: rows.filter(x => x.active).length };
      },
      cashClosures(startDate, endDate) {
        const rows = api.reports.rangeFilter(read().cashSessions.filter(x => x.status === 'CERRADO'), startDate, endDate, 'openedAt');
        return { rows: clone(rows), totalSales: rows.reduce((a,b) => a + (b.totalSales || 0), 0) };
      },
      orders(startDate, endDate) {
        const rows = api.reports.rangeFilter(read().orders, startDate, endDate);
        return { rows: clone(rows), totalOrders: rows.length };
      },
      exportCsv(filename, rows) {
        const headers = Object.keys(rows[0] || {});
        const csv = [headers.join(',')]
          .concat(rows.map(row => headers.map(h => JSON.stringify(row[h] ?? '')).join(',')))
          .join('\n');
        const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        const state = read();
        state.reportAudit.push({ id: randId('rep'), filename, createdAt: nowIso(), responsible: api.currentUser()?.email || 'Sistema' });
        addLog(state, 'EXPORTAR_REPORTE', `Exportación de ${filename}`, api.currentUser()?.email || 'Sistema');
        write(state);
      }
    },
    logs() { return clone(read().logs.sort((a,b) => new Date(b.createdAt) - new Date(a.createdAt))); },
    geo: {
      origin() { return clone(ORIGIN_HUB); },
      presets() { return clone(DESTINATION_PRESETS); },
      resolveDestination(address, fallback) { return clone(resolveDestination(address, fallback)); }
    },
    utils: { crc }
  };

  window.BakeSmartStore = api;
})();
