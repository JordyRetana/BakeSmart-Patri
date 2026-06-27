(function () {
    'use strict';

    const $ = (selector, context = document) => context.querySelector(selector);
    const $$ = (selector, context = document) => Array.from(context.querySelectorAll(selector));

    const app = window.app || (window.app = {});

    app.nav = {
        markActive() {
            const path = (location.pathname || "/").toLowerCase();
            $$("[data-nav]").forEach(link => {
                const href = (link.getAttribute("href") || "").toLowerCase();
                if (!href) return;
                const isActive = href === "/" ? path === "/" : path.startsWith(href);
                link.classList.toggle("active", isActive);
            });
        }
    };

    app.mobileNav = {
        init() {
            const nav = $('.navbar');
            const menu = $('#navbarMenu');

            if (!nav || !menu) return;

            menu.classList.add('mobile-nav-drawer');
            menu.setAttribute('aria-hidden', 'true');

            if (!$('#mobileNavRuntimeFix')) {
                const style = document.createElement('style');
                style.id = 'mobileNavRuntimeFix';
                style.textContent = `
                    @media (max-width: 860px) {
                        .navbar-container > .mobile-nav-toggle { display: none !important; }
                        .mobile-nav-fab {
                            display: inline-flex !important;
                            position: fixed !important;
                            align-items: center !important;
                            justify-content: center !important;
                            width: 56px !important;
                            height: 56px !important;
                            right: 1rem !important;
                            bottom: calc(1rem + env(safe-area-inset-bottom)) !important;
                            z-index: 6200 !important;
                            color: #fff !important;
                            border: 0 !important;
                            border-radius: 18px !important;
                            background: linear-gradient(135deg, #7c3aed, #db2777) !important;
                            box-shadow: 0 22px 46px rgba(139, 92, 246, .34) !important;
                        }
                        body.mobile-nav-open .mobile-nav-fab {
                            transform: translateY(-2px) scale(.96) !important;
                            box-shadow: 0 16px 36px rgba(139, 92, 246, .28) !important;
                        }
                        body.mobile-nav-open .mobile-nav-drawer {
                            left: auto !important;
                            right: .75rem !important;
                            top: auto !important;
                            bottom: calc(4.75rem + env(safe-area-inset-bottom)) !important;
                            width: min(350px, calc(100vw - 1.5rem)) !important;
                            max-height: min(62dvh, 420px) !important;
                            height: auto !important;
                            border-radius: 20px !important;
                            padding: .72rem !important;
                        }
                    }
                `;
                document.head.appendChild(style);
            }

            if (!$('.mobile-nav-fab')) {
                const fab = document.createElement('button');
                fab.type = 'button';
                fab.className = 'mobile-nav-toggle mobile-nav-fab';
                fab.setAttribute('aria-controls', 'navbarMenu');
                fab.setAttribute('aria-expanded', 'false');
                fab.setAttribute('aria-label', 'Abrir navegación');
                fab.innerHTML = '<i class="fas fa-bars"></i>';
                document.body.appendChild(fab);
            }

            const toggles = $$('.mobile-nav-toggle');
            if (!toggles.length) return;

            const setOpen = (open) => {
                nav.classList.toggle('navbar-open', open);
                document.body.classList.toggle('mobile-nav-open', open);
                menu.setAttribute('aria-hidden', open ? 'false' : 'true');

                toggles.forEach(button => {
                    button.setAttribute('aria-expanded', open ? 'true' : 'false');
                    const icon = $('i', button);
                    if (icon) icon.className = open ? 'fas fa-xmark' : 'fas fa-bars';
                });
            };

            toggles.forEach(button => {
                button.addEventListener('click', event => {
                    event?.preventDefault?.();
                    setOpen(!nav.classList.contains('navbar-open'));
                });
            });

            document.addEventListener('click', event => {
                if (!document.body.classList.contains('mobile-nav-open')) return;
                const target = event.target;
                if (target.closest('a') && target.closest('#navbarMenu')) {
                    setTimeout(() => setOpen(false), 200);
                    return;
                }
                if (target.closest('#navbarMenu, .mobile-nav-toggle')) return;
                setOpen(false);
            });

            window.addEventListener('keydown', event => {
                if (event.key === 'Escape') setOpen(false);
            });
        }
    };

    app.theme = {
        key: "bakesmart.theme",

        init() {
            this.load();
            this.setupSystemListener();
        },

        load() {
            const saved = localStorage.getItem(this.key);
            const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;

            if (saved === 'dark' || (!saved && prefersDark)) {
                document.body.classList.add('dark');
                document.documentElement.classList.add('dark-start');
                this.updateIcon('dark');
            } else {
                document.body.classList.remove('dark');
                document.documentElement.classList.remove('dark-start');
                this.updateIcon('light');
            }
        },

        toggle() {
            document.body.classList.toggle('dark');
            const isDark = document.body.classList.contains('dark');
            document.documentElement.classList.toggle('dark-start', isDark);
            localStorage.setItem(this.key, isDark ? 'dark' : 'light');
            this.updateIcon(isDark ? 'dark' : 'light');

            app.toast.show(
                isDark ? 'La interfaz cambió a tonos oscuros.' : 'La interfaz volvió a tonos claros.',
                'info',
                {
                    title: isDark ? 'Modo noche activado' : 'Modo claro activado',
                    duration: 2800
                }
            );
        },

        updateIcon(mode) {
            const themeBtn = $('.btn-ghost i.fa-moon, .btn-ghost i.fa-sun, .storefront-theme i.fa-moon, .storefront-theme i.fa-sun');
            if (themeBtn) {
                themeBtn.className = mode === 'dark' ? 'fas fa-sun' : 'fas fa-moon';
            }
        },

        setupSystemListener() {
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
                if (!localStorage.getItem(this.key)) {
                    document.body.classList.toggle('dark', e.matches);
                    this.updateIcon(e.matches ? 'dark' : 'light');
                }
            });
        }
    };

    const toastMeta = {
        success: { icon: 'fa-check', title: 'Listo', accent: '#22c55e' },
        error: { icon: 'fa-triangle-exclamation', title: 'Algo no salió bien', accent: '#ef4444' },
        warning: { icon: 'fa-exclamation', title: 'Revisar', accent: '#f59e0b' },
        info: { icon: 'fa-circle-info', title: 'Información', accent: '#8b5cf6' }
    };

    const cleanToastMessage = (message) => {
        const value = String(message || '').trim();
        const direct = {
            'Esta accion debe actualizarse desde el sistema.': 'La información quedó preparada para revisión.',
            'Esta acción debe actualizarse desde el sistema.': 'La información quedó preparada para revisión.',
            'Esta accion debe crearse desde el sistema.': 'El registro quedó preparado para revisión.',
            'Esta acción debe crearse desde el sistema.': 'El registro quedó preparado para revisión.',
            'Esta accion debe guardarse desde el sistema.': 'Los cambios quedaron preparados para revisión.',
            'Actualizar perfil debe guardarse desde el sistema.': 'Tu perfil quedó preparado para actualización.',
            'Crear promociones debe hacerse desde el formulario del sistema.': 'La promoción debe completarse desde este formulario.',
            'La marca de cliente frecuente debe actualizarse desde el sistema.': 'El cliente quedó marcado para revisión de fidelización.'
        };

        return (direct[value] || value)
            .replace(/\s+(en|desde)\s+SQL\s*Server\.?/gi, '.')
            .replace(/\s+(en|desde)\s+SQL\.?/gi, '.')
            .replace(/\s{2,}/g, ' ')
            .trim();
    };

    const escapeToastHtml = (value) => String(value || '').replace(/[&<>"']/g, char => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[char]));

    app.toast = {
        ensureStyles() {
            if ($('#toastPremiumStyles')) return;

            const style = document.createElement('style');
            style.id = 'toastPremiumStyles';
            style.textContent = `
                #toastContainer {
                    display: grid !important;
                    gap: .72rem !important;
                    width: min(420px, calc(100vw - 1.5rem)) !important;
                    pointer-events: none !important;
                    z-index: 12000 !important;
                }
                .app-toast {
                    --toast-accent: #8b5cf6;
                    position: relative;
                    display: grid;
                    grid-template-columns: 44px 1fr auto;
                    align-items: center;
                    gap: .85rem;
                    width: 100%;
                    min-height: 76px;
                    padding: .9rem .88rem;
                    overflow: hidden;
                    border: 1px solid rgba(139, 92, 246, .18);
                    border-radius: 22px;
                    color: #1f2937;
                    background:
                        linear-gradient(135deg, rgba(255,255,255,.96), rgba(255,247,253,.9)),
                        radial-gradient(circle at top left, color-mix(in srgb, var(--toast-accent) 18%, transparent), transparent 42%);
                    box-shadow: 0 24px 70px rgba(87, 60, 124, .18);
                    pointer-events: auto;
                    animation: toastSlideIn .32s cubic-bezier(.2, .9, .25, 1.2);
                    backdrop-filter: blur(18px);
                }
                .app-toast::before {
                    content: "";
                    position: absolute;
                    inset: 0 auto 0 0;
                    width: 5px;
                    background: linear-gradient(180deg, var(--toast-accent), #d946ef);
                }
                .app-toast__icon {
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    width: 44px;
                    height: 44px;
                    border-radius: 16px;
                    color: white;
                    background: linear-gradient(135deg, var(--toast-accent), #d946ef);
                    box-shadow: 0 14px 32px color-mix(in srgb, var(--toast-accent) 32%, transparent);
                }
                .app-toast__title {
                    margin: 0 0 .12rem;
                    font-size: .88rem;
                    font-weight: 900;
                    letter-spacing: .02em;
                    color: #111827;
                }
                .app-toast__message {
                    margin: 0;
                    color: #5b6473;
                    font-size: .88rem;
                    line-height: 1.35;
                }
                .app-toast__close {
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    width: 34px;
                    height: 34px;
                    border: 0;
                    border-radius: 999px;
                    color: #6b7280;
                    background: rgba(15, 23, 42, .06);
                    cursor: pointer;
                    transition: transform .2s ease, background .2s ease, color .2s ease;
                }
                .app-toast__close:hover {
                    transform: scale(1.05);
                    color: #111827;
                    background: rgba(15, 23, 42, .1);
                }
                .app-toast.toast-exit {
                    animation: toastSlideOut .24s ease forwards;
                }
                body.dark .app-toast {
                    color: #f8fafc;
                    border-color: rgba(196, 181, 253, .2);
                    background:
                        linear-gradient(135deg, rgba(17, 24, 39, .96), rgba(31, 22, 48, .92)),
                        radial-gradient(circle at top left, color-mix(in srgb, var(--toast-accent) 22%, transparent), transparent 44%);
                    box-shadow: 0 26px 80px rgba(0, 0, 0, .42);
                }
                body.dark .app-toast__title { color: #fff; }
                body.dark .app-toast__message { color: #d7dce8; }
                body.dark .app-toast__close {
                    color: #d8d2f7;
                    background: rgba(255,255,255,.08);
                }
                @keyframes toastSlideIn {
                    from { opacity: 0; transform: translate3d(22px, 10px, 0) scale(.96); }
                    to { opacity: 1; transform: translate3d(0, 0, 0) scale(1); }
                }
                @keyframes toastSlideOut {
                    to { opacity: 0; transform: translate3d(18px, 8px, 0) scale(.96); }
                }
                @media (max-width: 640px) {
                    #toastContainer {
                        right: .75rem !important;
                        bottom: calc(5.2rem + env(safe-area-inset-bottom)) !important;
                        width: calc(100vw - 1.5rem) !important;
                    }
                    .app-toast {
                        grid-template-columns: 40px 1fr auto;
                        min-height: 68px;
                        border-radius: 18px;
                        padding: .78rem;
                    }
                    .app-toast__icon {
                        width: 40px;
                        height: 40px;
                        border-radius: 14px;
                    }
                }
            `;
            document.head.appendChild(style);
        },

        show(message, type = 'info', options = 4000) {
            const container = $('#toastContainer');
            if (!container) return;

            this.ensureStyles();
            const config = typeof options === 'number' ? { duration: options } : (options || {});
            const meta = toastMeta[type] || toastMeta.info;
            const duration = Number(config.duration || 4000);
            const title = escapeToastHtml(config.title || meta.title);
            const body = escapeToastHtml(cleanToastMessage(message));

            const toast = document.createElement('div');
            toast.className = 'app-toast';
            toast.dataset.type = type;
            toast.style.setProperty('--toast-accent', meta.accent);

            toast.innerHTML = `
                <span class="app-toast__icon"><i class="fas ${meta.icon}"></i></span>
                <span>
                    <strong class="app-toast__title">${title}</strong>
                    <p class="app-toast__message">${body}</p>
                </span>
                <button class="app-toast__close" type="button" aria-label="Cerrar alerta">
                    <i class="fas fa-xmark"></i>
                </button>
            `;

            $('.app-toast__close', toast)?.addEventListener('click', () => this.dismiss(toast));
            container.appendChild(toast);

            setTimeout(() => {
                this.dismiss(toast);
            }, duration);
        },

        dismiss(toast) {
            if (!toast?.parentNode) return;
            toast.classList.add('toast-exit');
            setTimeout(() => toast.remove(), 240);
        },

        success(message, options) {
            this.show(message, 'success', options);
        },

        error(message, options) {
            this.show(message, 'error', options);
        },

        warning(message, options) {
            this.show(message, 'warning', options);
        },

        info(message, options) {
            this.show(message, 'info', options);
        }
    };

    app.modal = {
        _createShell({ maxWidth = '500px', closeOnBackdrop = true } = {}) {
            const existing = $('#modalContainer');
            if (existing) existing.remove();

            const container = document.createElement('div');
            container.id = 'modalContainer';
            container.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(15, 23, 42, 0.58);
                display: flex;
                align-items: center;
                justify-content: center;
                padding: 1rem;
                z-index: 10000;
                animation: fadeIn 0.25s ease;
            `;

            if (closeOnBackdrop) {
                container.addEventListener('click', (event) => {
                    if (event.target === container) this.close();
                });
            }

            const modal = document.createElement('div');
            modal.className = 'card app-modal-card';
            modal.style.cssText = `
                max-width: ${maxWidth};
                width: min(100%, ${maxWidth});
                max-height: calc(100vh - 2rem);
                overflow: hidden;
                animation: slideIn 0.25s ease;
            `;

            container.appendChild(modal);
            document.body.appendChild(container);
            return { container, modal };
        },

        create(options = {}) {
            const {
                title = 'Confirmar acción',
                message = '¿Estás seguro?',
                confirmText = 'Confirmar',
                cancelText = 'Cancelar',
                onConfirm,
                onCancel
            } = options;

            const { modal } = this._createShell();
            modal.innerHTML = `
                <div style="display:flex; justify-content:space-between; align-items:center; gap:1rem; margin-bottom:1rem;">
                    <h3 style="margin:0;">${title}</h3>
                    <button type="button" class="btn btn-ghost btn-sm" onclick="app.modal.close()">
                        <i class="fas fa-times"></i>
                    </button>
                </div>
                <p class="text-muted" style="margin-bottom:2rem;">${message}</p>
                <div style="display:flex; gap:1rem; justify-content:flex-end; flex-wrap:wrap;">
                    <button type="button" class="btn btn-outline" onclick="app.modal.cancel()">${cancelText}</button>
                    <button type="button" class="btn btn-primary" onclick="app.modal.confirm()">${confirmText}</button>
                </div>
            `;

            this._onConfirm = onConfirm;
            this._onCancel = onCancel;
        },

        open(options = {}) {
            const {
                title = 'Detalle',
                content = '',
                maxWidth = '760px',
                headerActions = '',
                closeOnBackdrop = true
            } = options;

            const { modal } = this._createShell({ maxWidth, closeOnBackdrop });
            modal.innerHTML = `
                <div class="app-modal-header" style="display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; margin-bottom:1rem;">
                    <div>
                        <h3 style="margin:0;">${title}</h3>
                    </div>
                    <div style="display:flex; align-items:center; gap:.75rem;">
                        ${headerActions || ''}
                        <button type="button" class="btn btn-ghost btn-sm" onclick="app.modal.close()">
                            <i class="fas fa-times"></i>
                        </button>
                    </div>
                </div>
                <div class="app-modal-body" style="overflow:auto; max-height:calc(100vh - 9rem); padding-right:.25rem;">${content}</div>
            `;

            this._onConfirm = null;
            this._onCancel = null;
            app.copy?.init?.();
        },

        confirm() {
            if (this._onConfirm) this._onConfirm();
            this.close();
        },

        cancel() {
            if (this._onCancel) this._onCancel();
            this.close();
        },

        close() {
            const modal = $('#modalContainer');
            if (modal) {
                modal.style.animation = 'fadeOut 0.25s ease';
                setTimeout(() => modal.remove(), 220);
            }
        }
    };

    app.dropdown = {
        init() {
            const dropdowns = $$('.dropdown');
            if (!dropdowns.length) return;

            const closeAll = (except = null) => {
                dropdowns.forEach(dropdown => {
                    if (dropdown !== except) {
                        dropdown.classList.remove('dropdown-open');
                        const toggle = $('.dropdown-toggle', dropdown);
                        if (toggle) toggle.setAttribute('aria-expanded', 'false');
                    }
                });
            };

            dropdowns.forEach(dropdown => {
                const toggle = $('.dropdown-toggle', dropdown);
                if (!toggle) return;

                toggle.setAttribute('role', 'button');
                toggle.setAttribute('tabindex', '0');
                toggle.setAttribute('aria-expanded', 'false');

                const toggleDropdown = (event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    const willOpen = !dropdown.classList.contains('dropdown-open');
                    closeAll(dropdown);
                    dropdown.classList.toggle('dropdown-open', willOpen);
                    toggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
                };

                toggle.addEventListener('click', toggleDropdown);
                toggle.addEventListener('keydown', (event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                        toggleDropdown(event);
                    }
                });
            });

            document.addEventListener('click', (event) => {
                if (!event.target.closest('.dropdown')) closeAll();
            });

            document.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') closeAll();
            });
        }
    };

    app.copy = {
        init() {
            const replacements = new Map([
                ['Guardar en SQL', 'Guardar producto'],
                ['Inventario guardado en SQL.', 'Inventario guardado correctamente.'],
                ['Movimiento registrado en SQL.', 'Movimiento registrado correctamente.'],
                ['Esta accion debe actualizarse desde el sistema.', 'La información queda preparada para revisión.'],
                ['Esta acción debe actualizarse desde el sistema.', 'La información queda preparada para revisión.'],
                ['Actualizar perfil debe guardarse desde el sistema.', 'Actualizar perfil']
            ]);

            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
            const nodes = [];
            while (walker.nextNode()) nodes.push(walker.currentNode);

            nodes.forEach(node => {
                const value = node.nodeValue;
                if (!value) return;
                let next = value;
                replacements.forEach((to, from) => {
                    next = next.replaceAll(from, to);
                });
                if (next !== value) node.nodeValue = next;
            });
        }
    };

    app.validate = {
        required(value) {
            return value && value.trim().length > 0;
        },

        email(value) {
            const re = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
            return re.test(value);
        },

        minLength(value, min) {
            return value && value.length >= min;
        },

        maxLength(value, max) {
            return value && value.length <= max;
        },

        number(value) {
            return !isNaN(parseFloat(value)) && isFinite(value);
        },

        phone(value) {
            const re = /^[\+]?[(]?[0-9]{3}[)]?[-\s\.]?[0-9]{3}[-\s\.]?[0-9]{4,6}$/;
            return re.test(value);
        },

        form(formElement, rules) {
            const errors = [];
            const formData = new FormData(formElement);

            for (const [field, fieldRules] of Object.entries(rules)) {
                const value = formData.get(field) || '';

                for (const rule of fieldRules) {
                    let isValid = true;
                    let message = '';

                    if (typeof rule === 'string') {
                        isValid = this[rule](value);
                        message = `El campo ${field} es inválido`;
                    } else if (rule.rule && rule.message) {
                        isValid = this[rule.rule](value, rule.params);
                        message = rule.message;
                    }

                    if (!isValid) {
                        errors.push({ field, message });

                        const input = formElement.querySelector(`[name="${field}"]`);
                        if (input) {
                            input.classList.add('input-error');
                        }
                        break;
                    } else {
                        const input = formElement.querySelector(`[name="${field}"]`);
                        if (input) {
                            input.classList.remove('input-error');
                        }
                    }
                }
            }

            return errors;
        }
    };

    app.dataTable = {
        init(tableElement, options = {}) {
            if (!tableElement) return;

            const {
                searchable = true,
                sortable = true,
                pagination = true,
                pageSize = 10
            } = options;

            if (searchable) {
                const searchDiv = document.createElement('div');
                searchDiv.style.cssText = 'margin-bottom: 1rem; display: flex; gap: 1rem;';
                searchDiv.innerHTML = `
                    <div style="flex: 1;">
                        <input type="text" class="input" placeholder="Buscar..." id="tableSearch">
                    </div>
                `;
                tableElement.parentNode.insertBefore(searchDiv, tableElement);

                const searchInput = $('#tableSearch');
                searchInput.addEventListener('input', (e) => {
                    this.filter(tableElement, e.target.value);
                });
            }
        },

        filter(tableElement, searchTerm) {
            const rows = $$('tbody tr', tableElement);
            const term = searchTerm.toLowerCase();

            rows.forEach(row => {
                const text = row.textContent.toLowerCase();
                row.style.display = text.includes(term) ? '' : 'none';
            });
        },

        sort(tableElement, columnIndex, ascending = true) {
            const tbody = $('tbody', tableElement);
            const rows = Array.from($$('tr', tbody));

            rows.sort((a, b) => {
                const aVal = a.children[columnIndex].textContent.trim();
                const bVal = b.children[columnIndex].textContent.trim();

                if (ascending) {
                    return aVal.localeCompare(bVal);
                } else {
                    return bVal.localeCompare(aVal);
                }
            });

            tbody.innerHTML = '';
            rows.forEach(row => tbody.appendChild(row));
        }
    };

    app.api = {
        async get(url) {
            try {
                const response = await fetch(url);
                if (!response.ok) throw new Error('Network response was not ok');
                return await response.json();
            } catch (error) {
                console.error('API Error:', error);
                app.toast.error('Error al cargar los datos');
                throw error;
            }
        },

        async post(url, data) {
            try {
                const response = await fetch(url, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': $('input[name="__RequestVerificationToken"]')?.value
                    },
                    body: JSON.stringify(data)
                });

                if (!response.ok) throw new Error('Network response was not ok');
                return await response.json();
            } catch (error) {
                console.error('API Error:', error);
                app.toast.error('Error al guardar los datos');
                throw error;
            }
        }
    };

    app.loader = {
        show() {
            let loader = $('#globalLoader');
            if (!loader) {
                loader = document.createElement('div');
                loader.id = 'globalLoader';
                loader.style.cssText = `
                    position: fixed;
                    top: 0;
                    left: 0;
                    right: 0;
                    bottom: 0;
                    background: rgba(0,0,0,0.5);
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    z-index: 20000;
                `;
                loader.innerHTML = '<div class="spinner"></div>';
                document.body.appendChild(loader);
            }
        },

        hide() {
            const loader = $('#globalLoader');
            if (loader) loader.remove();
        }
    };


    app.page = {
        ready(fn) {
            const run = () => Promise.resolve(fn()).catch(error => app.toast?.error?.(error.message));
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', run, { once: true });
            } else {
                run();
            }
        }
    };

    app.navigation = {
        _busy: false,
        _abortController: new AbortController(),

        init() {
            // Cada módulo carga su layout, permisos, estilos y scripts completos.
            // La navegación parcial anterior dejaba elementos de la vista previa.
        },

        getAbortSignal() {
            return this._abortController.signal;
        },

        resetAbortSignal() {
            this._abortController.abort();
            this._abortController = new AbortController();
        },

        shouldHandle(link, event) {
            if (!link || link.isContentEditable) return false;
            if (event.defaultPrevented || event.button !== 0) return false;
            if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return false;
            if (link.target && link.target !== '_self') return false;
            if (link.hasAttribute('download') || link.dataset.fullReload !== undefined || link.dataset.noSpa !== undefined) return false;

            const href = link.getAttribute('href');
            if (!href || href.startsWith('#') || href.startsWith('mailto:') || href.startsWith('tel:') || href.startsWith('javascript:')) {
                return false;
            }

            if (link.closest('form') || link.closest('[data-confirm]')) return false;

            const url = new URL(href, location.href);
            return url.origin === location.origin;
        },

        handleClick(event) {
            const link = event.target.closest('a[href]');
            if (!this.shouldHandle(link, event)) return;

            event.preventDefault();
            const url = new URL(link.getAttribute('href'), location.href);
            this.navigate(url.pathname + url.search);
        },

        async navigate(url, pushState = true) {
            const targetPath = url.startsWith('http')
                ? new URL(url).pathname + new URL(url).search
                : url;
            if (targetPath !== location.pathname + location.search) {
                window.location.assign(targetPath);
            }
        },

        updatePageStyles(doc) {
            $$('link[data-page-style]').forEach(link => link.remove());

            doc.querySelectorAll('head link[rel="stylesheet"]').forEach(link => {
                const href = link.getAttribute('href');
                if (!href || href.includes('site.css') || href.includes('font-awesome') || href.includes('fonts.googleapis')) {
                    return;
                }

                if (!href.includes('/css/pages/') && !href.includes('leaflet')) {
                    return;
                }

                const stylesheet = document.createElement('link');
                stylesheet.rel = 'stylesheet';
                stylesheet.href = href;
                stylesheet.dataset.pageStyle = 'true';

                if (link.crossOrigin) stylesheet.crossOrigin = link.crossOrigin;
                if (link.integrity) stylesheet.integrity = link.integrity;

                document.head.appendChild(stylesheet);
            });
        },

        async runPageScripts(doc) {
            const skipSrc = /site\.js|bakesmart-store\.js|jquery|bootstrap\.bundle/i;
            const scripts = [...doc.body.querySelectorAll('script')].filter(script => {
                const src = script.getAttribute('src') || '';
                return !skipSrc.test(src);
            });

            for (const script of scripts) {
                await new Promise((resolve, reject) => {
                    const el = document.createElement('script');

                    [...script.attributes].forEach(attr => {
                        el.setAttribute(attr.name, attr.value);
                    });

                    if (script.src) {
                        const srcUrl = new URL(script.src, location.href).href;
                        const alreadyLoaded = [...document.scripts].some(existing => existing.src === srcUrl);
                        if (alreadyLoaded) {
                            resolve();
                            return;
                        }

                        el.onload = () => resolve();
                        el.onerror = () => reject(new Error('No se pudo cargar un script de la página.'));
                        document.body.appendChild(el);
                        return;
                    }

                    el.textContent = script.textContent;
                    document.body.appendChild(el);
                    el.remove();
                    resolve();
                });
            }
        }
    };

    app.motion = {
        init() {
            const homePage = (location.pathname || '/') === '/';
            const operationalPage = document.querySelector('.dashboard-page, .production-page, .orders-page, .inventory-page, .pos-page, .accounting-page, .marketing-page, .users-workspace');

            if (!homePage || operationalPage) {
                document.body.classList.add('no-heavy-motion');
                this.initLight();
                this.hideLoader();
                return;
            }

            this.decorateCards();
            this.decorateStats();
            this.revealOnScroll();
            this.spawnParticles();
            this.setupCursorGlow();
            this.hideLoader();
        },

        initLight() {
            document.body.classList.add('no-heavy-motion');
            $$('.reveal-on-scroll').forEach(el => el.classList.add('revealed'));
            this.hideLoader();
        },

        decorateCards() {
            const selectors = [
                '.card',
                '.table-container',
                '.page-header',
                '.hero-section .container > div',
                '.stats-section .card',
                '.featured-section .card'
            ];

            const seen = new Set();
            selectors.forEach(selector => {
                $$(selector).forEach((el, index) => {
                    if (seen.has(el)) return;
                    seen.add(el);
                    if (!el.classList.contains('reveal-on-scroll')) {
                        el.classList.add('reveal-on-scroll');
                    }
                    el.style.setProperty('--reveal-delay', `${Math.min(index * 70, 420)}ms`);

                    if (el.classList.contains('card')) {
                        const textBlocks = el.querySelectorAll('h2, h3, h4, h5, .display-4');
                        const firstMetric = Array.from(textBlocks).find(node => /^\s*[\d,.]+/.test(node.textContent || ''));
                        if (firstMetric) {
                            el.classList.add('is-kpi');
                        }

                        if (!el.querySelector('.metric-spark') && (el.classList.contains('is-kpi') || index < 8)) {
                            const spark = document.createElement('div');
                            spark.className = 'metric-spark';
                            spark.innerHTML = `
                                <svg viewBox="0 0 120 120" fill="none" aria-hidden="true">
                                    <path d="M5 84C20 86 33 40 49 48C59 53 65 89 79 84C92 79 97 34 115 39" stroke="url(#sparkGradient)" stroke-width="6" stroke-linecap="round"/>
                                    <defs>
                                        <linearGradient id="sparkGradient" x1="5" y1="39" x2="115" y2="84" gradientUnits="userSpaceOnUse">
                                            <stop stop-color="#8b5cf6"/>
                                            <stop offset="1" stop-color="#ec4899"/>
                                        </linearGradient>
                                    </defs>
                                </svg>`;
                            el.appendChild(spark);
                        }
                    }
                });
            });

            $$('h1, .page-header h1, .section-title h2').forEach(el => el.classList.add('text-gradient'));
        },

        decorateStats() {
            const numberRegex = /^-?\d+(?:[.,]\d+)?(?:\+|%|x)?$/i;

            $$('h2, h3, .metric-value, .card strong, .stats-section .card div').forEach((el, index) => {
                const text = (el.textContent || '').trim();
                if (!text || text.length > 12 || !numberRegex.test(text)) return;

                const cleaned = text.replace(/[^\d.,-]/g, '').replace(',', '.');
                const value = Number.parseFloat(cleaned);
                if (Number.isNaN(value)) return;

                const suffix = text.replace(/[-\d.,]/g, '');
                el.dataset.countTo = String(value);
                el.dataset.countSuffix = suffix;
                el.dataset.countDecimals = cleaned.includes('.') ? String((cleaned.split('.')[1] || '').length) : '0';
                el.classList.add('metric-value');
                el.textContent = '0' + suffix;
                el.style.setProperty('--reveal-delay', `${Math.min(index * 45, 280)}ms`);
            });

            const animate = (entry) => {
                const el = entry.target;
                if (el.dataset.animated === 'true') return;
                const target = Number.parseFloat(el.dataset.countTo || '0');
                const decimals = Number.parseInt(el.dataset.countDecimals || '0', 10);
                const suffix = el.dataset.countSuffix || '';
                const duration = 1300;
                const start = performance.now();

                const step = (now) => {
                    const progress = Math.min((now - start) / duration, 1);
                    const eased = 1 - Math.pow(1 - progress, 3);
                    const value = target * eased;
                    el.textContent = value.toFixed(decimals).replace(/\.0+$/, '') + suffix;
                    if (progress < 1) {
                        requestAnimationFrame(step);
                    } else {
                        el.dataset.animated = 'true';
                        el.textContent = (decimals ? target.toFixed(decimals) : Math.round(target).toString()) + suffix;
                    }
                };

                requestAnimationFrame(step);
            };

            const observer = new IntersectionObserver((entries) => {
                entries.forEach(entry => {
                    if (entry.isIntersecting) animate(entry);
                });
            }, { threshold: 0.45 });

            $$('[data-count-to]').forEach(el => observer.observe(el));
        },

        revealOnScroll() {
            const observer = new IntersectionObserver((entries) => {
                entries.forEach(entry => {
                    if (!entry.isIntersecting) return;
                    entry.target.classList.add('revealed');
                    observer.unobserve(entry.target);
                });
            }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });

            $$('.reveal-on-scroll').forEach(el => observer.observe(el));
        },

        spawnParticles() {
            const container = $('#bgParticles');
            if (!container || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

            const particleCount = window.innerWidth < 768 ? 10 : 18;
            const page = (document.body.dataset.page || '').toLowerCase();
            const palette = page.includes('dashboard')
                ? ['rgba(139,92,246,.70)', 'rgba(236,72,153,.65)', 'rgba(16,185,129,.55)']
                : ['rgba(236,72,153,.60)', 'rgba(139,92,246,.60)', 'rgba(245,158,11,.50)'];

            container.innerHTML = '';
            for (let i = 0; i < particleCount; i++) {
                const p = document.createElement('span');
                p.className = 'bg-particle';
                const size = 8 + Math.random() * 26;
                p.style.width = `${size}px`;
                p.style.height = `${size}px`;
                p.style.left = `${Math.random() * 100}%`;
                p.style.top = `${55 + Math.random() * 45}%`;
                p.style.animationDuration = `${10 + Math.random() * 12}s`;
                p.style.animationDelay = `${Math.random() * 6}s`;
                p.style.background = `linear-gradient(135deg, ${palette[i % palette.length]}, rgba(255,255,255,.15))`;
                container.appendChild(p);
            }
        },

        setupCursorGlow() {
            const glow = $('.cursor-glow');
            if (!glow || window.innerWidth < 1024) return;

            const move = (event) => {
                document.body.classList.add('cursor-active');
                glow.style.left = `${event.clientX}px`;
                glow.style.top = `${event.clientY}px`;
            };

            window.addEventListener('mousemove', move, { passive: true });
            document.addEventListener('mouseleave', () => document.body.classList.remove('cursor-active'));
        },

        hideLoader() {
            const loader = $('#pageLoader');
            if (!loader) return;
            const dismiss = () => loader.classList.add('is-hidden');
            window.addEventListener('load', () => setTimeout(dismiss, 350), { once: true });
            setTimeout(dismiss, 1400);
        }
    };


    app.userMenu = {
        _docListener: null,

        init() {
            const toggle = document.getElementById('userMenuToggle');
            const menu = document.getElementById('userMenu');
            const dropdown = document.getElementById('userDropdown');

            if (!toggle || !menu || !dropdown) return;

            const self = this;

            const closeMenu = () => {
                menu.classList.remove('user-menu-open');
                dropdown.hidden = true;
                toggle.setAttribute('aria-expanded', 'false');
                self._detachDocListener();
            };

            const openMenu = () => {
                menu.classList.add('user-menu-open');
                dropdown.hidden = false;
                toggle.setAttribute('aria-expanded', 'true');
                self._attachDocListener(menu, closeMenu);
            };

            toggle.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();

                const isOpen = menu.classList.contains('user-menu-open');
                if (isOpen) {
                    closeMenu();
                } else {
                    openMenu();
                }
            });

            // Cerrar con Escape
            document.addEventListener('keydown', (event) => {
                if (event.key === 'Escape' && menu.classList.contains('user-menu-open')) {
                    closeMenu();
                }
            });

            // Cerrar al hacer clic en cualquier enlace o boton dentro del dropdown
            dropdown.addEventListener('click', (event) => {
                if (event.target.closest('a, button[type="submit"]')) {
                    setTimeout(() => closeMenu(), 200);
                }
            });
        },

        _attachDocListener(menu, closeFn) {
            this._detachDocListener();
            this._docListener = (event) => {
                if (!menu.contains(event.target)) {
                    closeFn();
                }
            };
            document.addEventListener('click', this._docListener);
        },

        _detachDocListener() {
            if (this._docListener) {
                document.removeEventListener('click', this._docListener);
                this._docListener = null;
            }
        }
    };

    document.addEventListener('DOMContentLoaded', () => {
        app.theme.init();
        app.navigation.init();
        app.nav.markActive();
        app.mobileNav.init();
        app.dropdown.init();
        app.userMenu.init();
        app.copy.init();
        app.motion.init();

        $$('[data-confirm]').forEach(element => {
            element.addEventListener('click', (e) => {
                e.preventDefault();
                const message = element.dataset.confirm || '¿Estás seguro?';
                const href = element.getAttribute('href');

                app.modal.create({
                    title: 'Confirmar acción',
                    message: message,
                    onConfirm: () => {
                        if (href) app.navigation.navigate(href);
                    }
                });
            });
        });
    });

    app.utils = {
        formatCurrency(amount) {
            return new Intl.NumberFormat('es-CR', {
                style: 'currency',
                currency: 'CRC',
                minimumFractionDigits: 0
            }).format(amount);
        },

        formatDate(date) {
            return new Intl.DateTimeFormat('es-ES', {
                year: 'numeric',
                month: 'long',
                day: 'numeric'
            }).format(new Date(date));
        },

        formatDateTime(date) {
            return new Intl.DateTimeFormat('es-ES', {
                year: 'numeric',
                month: 'long',
                day: 'numeric',
                hour: '2-digit',
                minute: '2-digit'
            }).format(new Date(date));
        },

        debounce(func, wait) {
            let timeout;
            return function executedFunction(...args) {
                const later = () => {
                    clearTimeout(timeout);
                    func(...args);
                };
                clearTimeout(timeout);
                timeout = setTimeout(later, wait);
            };
        }
    };

    window.app = app;
})();
