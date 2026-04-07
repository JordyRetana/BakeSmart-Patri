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
                this.updateIcon('dark');
            } else {
                document.body.classList.remove('dark');
                this.updateIcon('light');
            }
        },

        toggle() {
            document.body.classList.toggle('dark');
            const isDark = document.body.classList.contains('dark');
            localStorage.setItem(this.key, isDark ? 'dark' : 'light');
            this.updateIcon(isDark ? 'dark' : 'light');

            app.toast.show(`Tema ${isDark ? 'oscuro' : 'claro'} activado`, 'info');
        },

        updateIcon(mode) {
            const themeBtn = $('.btn-ghost i.fa-moon, .btn-ghost i.fa-sun');
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

    app.toast = {
        show(message, type = 'info', duration = 4000) {
            const container = $('#toastContainer');
            if (!container) return;

            const icons = {
                success: 'fa-check-circle',
                error: 'fa-times-circle',
                warning: 'fa-exclamation-triangle',
                info: 'fa-info-circle'
            };

            const toast = document.createElement('div');
            toast.className = `alert alert-${type}`;
            toast.style.cssText = `
                margin-bottom: 0.5rem;
                animation: slideIn 0.3s ease;
                display: flex;
                align-items: center;
                gap: 0.75rem;
                min-width: 300px;
                box-shadow: var(--shadow-lg);
            `;

            toast.innerHTML = `
                <i class="fas ${icons[type] || 'fa-info-circle'}" style="font-size: 1.25rem;"></i>
                <div style="flex: 1;">${message}</div>
                <button onclick="this.parentElement.remove()" style="background: none; border: none; color: inherit; cursor: pointer; opacity: 0.7;">
                    <i class="fas fa-times"></i>
                </button>
            `;

            container.appendChild(toast);

            setTimeout(() => {
                if (toast.parentNode) {
                    toast.style.animation = 'slideOut 0.3s ease';
                    setTimeout(() => toast.remove(), 300);
                }
            }, duration);
        },

        success(message) {
            this.show(message, 'success');
        },

        error(message) {
            this.show(message, 'error');
        },

        warning(message) {
            this.show(message, 'warning');
        },

        info(message) {
            this.show(message, 'info');
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


    app.motion = {
        init() {
            this.decorateCards();
            this.decorateStats();
            this.revealOnScroll();
            this.spawnParticles();
            this.setupCursorGlow();
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


    document.addEventListener('DOMContentLoaded', () => {
        app.theme.init();

        app.nav.markActive();
        app.dropdown.init();
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
                        if (href) window.location.href = href;
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