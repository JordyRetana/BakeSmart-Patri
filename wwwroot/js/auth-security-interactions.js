(() => {
    document.querySelectorAll('.security-code-input').forEach(input => {
        input.addEventListener('input', () => {
            input.value = input.value.replace(/\D/g, '').slice(0, 6);
        });
    });

    document.querySelectorAll('.security-single-submit').forEach(form => {
        form.addEventListener('submit', event => {
            if (form.dataset.submitting === 'true') {
                event.preventDefault();
                return;
            }
            if (!form.checkValidity()) return;
            form.dataset.submitting = 'true';
            const button = form.querySelector('button[type="submit"]');
            if (button) {
                button.disabled = true;
                button.setAttribute('aria-busy', 'true');
                button.dataset.originalHtml = button.innerHTML;
                button.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Verificando…';
            }
        });
    });

    document.querySelector('[data-copy-target]')?.addEventListener('click', async function () {
        const value = document.getElementById(this.dataset.copyTarget)?.textContent?.trim();
        if (!value) return;
        try {
            await navigator.clipboard.writeText(value);
            this.innerHTML = '<i class="fas fa-check"></i> Copiada';
        } catch {
            this.innerHTML = '<i class="fas fa-circle-exclamation"></i> Seleccione y copie la clave';
        }
    });
})();
