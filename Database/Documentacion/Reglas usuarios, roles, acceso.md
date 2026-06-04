# Tarea 40 - Reglas Básicas de Seguridad de Usuarios y Roles

## 1. Reglas Generales de Usuarios

- Todo usuario debe tener un email único en el sistema.
- Todo usuario debe estar asociado a un Rol válido (Admin, Staff o Cliente).
- No se permite crear usuarios sin rol.
- La contraseña nunca se debe guardar en texto plano (siempre como PasswordHash).
- El campo Activo permite deshabilitar usuarios sin borrarlos.

## 2. Reglas de Roles

- Admin: Tiene acceso total al sistema.
- Staff: Puede gestionar pedidos, inventario, POS y reportes.
- Cliente: Solo puede ver su perfil y sus propios pedidos.
- No se permite eliminar roles que tengan usuarios asociados.

## 3. Reglas de Acceso y Autenticación

- El login se hace con email y contraseña.
- Después del login se debe validar el rol del usuario para mostrar solo las pantallas permitidas.
- Las contraseñas deben tener mínimo 8 caracteres (en el futuro se mejorará).
- Las sesiones deben tener tiempo de expiración (actualmente 12 horas en el AccountController).
- No se permite acceso sin autenticación (excepto páginas públicas).

## 4. Manejo Seguro de Información

- No se debe mostrar información sensible (como PasswordHash) en consultas SELECT.
- El PasswordHash nunca debe viajar en respuestas JSON.
- Solo el Admin puede crear, editar o desactivar usuarios.
- Los clientes solo pueden ver y modificar su propia información.
- Se debe registrar en bitácora (Logs) todas las acciones importantes (login, creación de usuario, cambios de rol, etc.).

## 5. Buenas Prácticas Recomendadas

- Usar HTTPS en toda la aplicación.
- Validar todos los inputs del usuario.
- Implementar Rate Limiting en login para evitar ataques de fuerza bruta.
- Hacer backups regulares de la base de datos.
- No hardcodear credenciales en el código (usar appsettings.json o secrets).

---

**Estado:** Documentación creada para apoyar el desarrollo del módulo de usuarios y roles.
**Fecha:** Junio 2026
**Responsable:** Ricardo Ulate