# Tarea 44 - Documentar relaciones y llaves foráneas

## Relaciones principales del módulo de Inventario

### 1. Tabla Categorias
- Llave Primaria: Id

### 2. Tabla Productos
- Llave Primaria: Id
- Llave Foránea: CategoriaId → Categorias(Id)

### 3. Tabla MovimientosInventario
- Llave Primaria: Id
- Llaves Foráneas: 
  - ProductoId → Productos(Id)
  - UsuarioId → Usuarios(Id)

## Reglas de Integridad

- No se puede crear un producto sin una categoría válida.
- No se puede crear un movimiento de inventario sin un producto existente.
- No se permite eliminar una categoría que tenga productos asociados.
- No se permite eliminar un producto que tenga movimientos de inventario.
- El Stock se debe actualizar mediante movimientos (no manualmente).

## Notas importantes

- Todas las relaciones usan FOREIGN KEY para mantener la integridad.
- Se agregaron índices en columnas frecuentes (ProductoId, CategoriaId).
- FechaVencimiento se agregó en Productos por ser productos alimenticios.
- El sistema debe controlar el stock a través de movimientos (no updates directos).

---

**Estado:** Documentación completada  
**Responsable:** Ricardo Ulate