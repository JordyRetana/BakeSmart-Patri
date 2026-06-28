(function () {
    'use strict';

    const LEAFLET_CSS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';
    const LEAFLET_JS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';
    const DEFAULT_CENTER = { lat: 9.9142, lng: -84.0734 };

    let leafletPromise = null;

    function loadStylesheet(href) {
        if (document.querySelector(`link[href="${href}"]`)) return Promise.resolve();
        return new Promise((resolve, reject) => {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = href;
            link.onload = () => resolve();
            link.onerror = () => reject(new Error('No se pudo cargar los estilos del mapa.'));
            document.head.appendChild(link);
        });
    }

    function loadScript(src) {
        if (window.L) return Promise.resolve();
        if (document.querySelector(`script[src="${src}"]`)) {
            return new Promise((resolve) => {
                const check = () => window.L ? resolve() : setTimeout(check, 40);
                check();
            });
        }
        return new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = src;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error('No se pudo cargar Leaflet.'));
            document.body.appendChild(script);
        });
    }

    function ensureLeaflet() {
        if (!leafletPromise) {
            leafletPromise = loadStylesheet(LEAFLET_CSS).then(() => loadScript(LEAFLET_JS));
        }
        return leafletPromise;
    }

    function isValidCoordinate(lat, lng) {
        const latitude = Number(lat);
        const longitude = Number(lng);
        return Number.isFinite(latitude) &&
            Number.isFinite(longitude) &&
            latitude >= -90 && latitude <= 90 &&
            longitude >= -180 && longitude <= 180 &&
            !(latitude === 0 && longitude === 0);
    }

    function debounce(fn, wait = 350) {
        let timer;
        return (...args) => {
            clearTimeout(timer);
            timer = setTimeout(() => fn(...args), wait);
        };
    }

    async function geoSearch(query) {
        const response = await fetch(`/api/geo/search?q=${encodeURIComponent(query)}`);
        if (!response.ok) throw new Error('No se pudo buscar la direccion.');
        return response.json();
    }

    async function geoReverse(lat, lng) {
        const response = await fetch(`/api/geo/reverse?lat=${encodeURIComponent(lat)}&lng=${encodeURIComponent(lng)}`);
        if (!response.ok) throw new Error('No se pudo obtener la direccion del punto seleccionado.');
        return response.json();
    }

    class MapPicker {
        constructor(container, options = {}) {
            this.container = typeof container === 'string' ? document.getElementById(container) : container;
            this.options = options;
            this.onChange = typeof options.onChange === 'function' ? options.onChange : null;
            this.map = null;
            this.marker = null;
            this.disabled = false;
            this.values = {
                label: options.label || '',
                address: options.address || '',
                lat: options.lat ?? null,
                lng: options.lng ?? null
            };
        }

        async init() {
            if (!this.container) throw new Error('Contenedor de mapa no encontrado.');
            await ensureLeaflet();

            this.container.innerHTML = `
                <div class="map-picker">
                    <div class="map-picker__search-row">
                        <div class="map-picker__search">
                            <label class="map-picker__field">
                                <span>Buscar direccion</span>
                                <input type="text" class="form-control map-picker-search-input" placeholder="Ej: Escazu, San Jose" autocomplete="off" />
                            </label>
                            <div class="map-picker__results"></div>
                        </div>
                        <button type="button" class="btn btn-outline map-picker-locate-btn">
                            <i class="fas fa-location-crosshairs"></i> Mi ubicacion
                        </button>
                    </div>
                    <div class="map-picker__canvas"></div>
                    <div class="map-picker__field">
                        <label>Direccion seleccionada</label>
                        <input type="text" class="form-control map-picker-address" readonly />
                    </div>
                    <div class="map-picker__coords">
                        <div>
                            <label>Latitud</label>
                            <input type="text" class="form-control map-picker-lat" readonly />
                        </div>
                        <div>
                            <label>Longitud</label>
                            <input type="text" class="form-control map-picker-lng" readonly />
                        </div>
                    </div>
                    <p class="map-picker__hint">Haz clic en el mapa o arrastra el marcador para ajustar la ubicacion.</p>
                    <p class="map-picker__error"></p>
                </div>
            `;

            this.searchInput = this.container.querySelector('.map-picker-search-input');
            this.resultsBox = this.container.querySelector('.map-picker__results');
            this.addressInput = this.container.querySelector('.map-picker-address');
            this.latInput = this.container.querySelector('.map-picker-lat');
            this.lngInput = this.container.querySelector('.map-picker-lng');
            this.errorBox = this.container.querySelector('.map-picker__error');
            this.canvas = this.container.querySelector('.map-picker__canvas');

            const center = isValidCoordinate(this.values.lat, this.values.lng)
                ? { lat: Number(this.values.lat), lng: Number(this.values.lng) }
                : DEFAULT_CENTER;

            this.map = L.map(this.canvas, { zoomControl: true, scrollWheelZoom: true }).setView([center.lat, center.lng], 14);
            L.tileLayer('https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png', {
                attribution: '&copy; OpenStreetMap &copy; CARTO',
                maxZoom: 19
            }).addTo(this.map);

            const icon = L.divIcon({
                className: '',
                html: '<div class="map-picker-marker"><i class="fas fa-location-dot"></i></div>',
                iconSize: [34, 34],
                iconAnchor: [17, 34]
            });

            this.marker = L.marker([center.lat, center.lng], { draggable: true, icon }).addTo(this.map);

            this.marker.on('dragend', () => this.handleMarkerMoved());
            this.map.on('click', (event) => {
                if (this.disabled) return;
                this.marker.setLatLng(event.latlng);
                this.handleMarkerMoved();
            });

            this.searchInput.addEventListener('input', debounce(() => this.runSearch()));
            this.container.querySelector('.map-picker-locate-btn').addEventListener('click', () => this.locateUser());
            document.addEventListener('click', (event) => {
                if (!this.container.contains(event.target)) this.closeResults();
            });

            if (this.values.address) {
                this.syncInputs();
            } else if (isValidCoordinate(this.values.lat, this.values.lng)) {
                await this.handleMarkerMoved(false);
            } else {
                this.syncInputs();
            }

            this.emitChange();
            setTimeout(() => this.map.invalidateSize(true), 120);
            return this;
        }

        setDisabled(disabled) {
            this.disabled = !!disabled;
            this.container.classList.toggle('map-picker--disabled', this.disabled);
            if (this.marker) {
                if (this.disabled) this.marker.dragging.disable();
                else this.marker.dragging.enable();
            }
        }

        setValues({ label, address, lat, lng } = {}) {
            if (label !== undefined) this.values.label = label;
            if (address !== undefined) this.values.address = address;
            if (lat !== undefined) this.values.lat = lat;
            if (lng !== undefined) this.values.lng = lng;

            if (this.marker && isValidCoordinate(this.values.lat, this.values.lng)) {
                const point = L.latLng(Number(this.values.lat), Number(this.values.lng));
                this.marker.setLatLng(point);
                this.map.setView(point, Math.max(this.map.getZoom(), 14));
            }
            this.syncInputs();
            this.clearError();
        }

        getValues() {
            return { ...this.values };
        }

        applyToFields(fieldMap = {}) {
            const values = this.getValues();
            const setValue = (selector, value) => {
                if (!selector) return;
                const el = selector.startsWith('#') || selector.startsWith('.')
                    ? document.querySelector(selector)
                    : document.getElementById(selector);
                if (el) el.value = value ?? '';
            };

            setValue(fieldMap.address, values.address || '');
            setValue(fieldMap.lat, values.lat ?? '');
            setValue(fieldMap.lng, values.lng ?? '');
            this.emitChange();
            return values;
        }

        isValid() {
            return isValidCoordinate(this.values.lat, this.values.lng) &&
                String(this.values.address || '').trim().length > 0;
        }

        showError(message) {
            this.errorBox.textContent = message;
            this.errorBox.classList.add('is-visible');
        }

        clearError() {
            this.errorBox.textContent = '';
            this.errorBox.classList.remove('is-visible');
        }

        syncInputs() {
            this.addressInput.value = this.values.address || '';
            this.latInput.value = this.values.lat ?? '';
            this.lngInput.value = this.values.lng ?? '';
            if (this.values.address) this.searchInput.value = this.values.address;
        }

        emitChange() {
            if (this.onChange) this.onChange(this.getValues());
        }

        async handleMarkerMoved(updateAddress = true) {
            const { lat, lng } = this.marker.getLatLng();
            this.values.lat = Number(lat.toFixed(6));
            this.values.lng = Number(lng.toFixed(6));
            this.syncInputs();
            this.clearError();

            if (updateAddress) {
                try {
                    const result = await geoReverse(this.values.lat, this.values.lng);
                    this.values.address = result.displayName || this.values.address;
                    this.syncInputs();
                } catch (_) { /* keep previous address */ }
            }

            this.emitChange();
        }

        closeResults() {
            this.resultsBox.classList.remove('is-open');
            this.resultsBox.innerHTML = '';
        }

        async runSearch() {
            const query = this.searchInput.value.trim();
            if (query.length < 3) {
                this.closeResults();
                return;
            }

            try {
                const results = await geoSearch(query);
                if (!results.length) {
                    this.resultsBox.innerHTML = '<div class="map-picker__result">Sin resultados</div>';
                    this.resultsBox.classList.add('is-open');
                    return;
                }

                this.resultsBox.innerHTML = results.map((item, index) => `
                    <button type="button" class="map-picker__result" data-index="${index}">
                        ${item.displayName}
                    </button>
                `).join('');
                this.resultsBox.classList.add('is-open');
                this._lastResults = results;

                this.resultsBox.querySelectorAll('.map-picker__result[data-index]').forEach(button => {
                    button.addEventListener('click', () => {
                        const result = this._lastResults[Number(button.dataset.index)];
                        this.selectResult(result);
                    });
                });
            } catch (error) {
                this.showError(error.message);
            }
        }

        selectResult(result) {
            this.values.lat = Number(Number(result.lat).toFixed(6));
            this.values.lng = Number(Number(result.lng).toFixed(6));
            this.values.address = result.displayName;
            this.values.label = result.displayName.split(',')[0] || this.values.label;
            this.setValues(this.values);
            this.closeResults();
            this.emitChange();
        }

        locateUser() {
            if (!navigator.geolocation) {
                this.showError('Tu navegador no permite obtener la ubicacion actual.');
                return;
            }

            navigator.geolocation.getCurrentPosition(
                async (position) => {
                    this.values.lat = Number(position.coords.latitude.toFixed(6));
                    this.values.lng = Number(position.coords.longitude.toFixed(6));
                    this.setValues(this.values);
                    await this.handleMarkerMoved();
                },
                () => this.showError('No se pudo obtener tu ubicacion. Selecciona un punto en el mapa.')
            );
        }

        destroy() {
            if (this.map) {
                this.map.remove();
                this.map = null;
            }
        }
    }

    window.BakeSmartMapPicker = {
        create(container, options) {
            const picker = new MapPicker(container, options);
            return picker.init();
        },
        isValidCoordinate
    };
})();
