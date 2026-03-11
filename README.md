# 🏆 Product Aggregator - Code Challenge

## 🎯 Escenario

Eres parte de un equipo que mantiene un servicio de agregación de productos. Este servicio consulta múltiples proveedores externos para obtener:

- **Precios** de 3 proveedores diferentes
- **Stock** de 2 regiones (East/West coast)

El servicio actual tiene problemas de rendimiento. Tu tarea es identificar los problemas y optimizarlo.

## 📁 Estructura del Proyecto

```
src/
├── ProductAggregator.Api/
│   ├── Controllers/
│   │   └── ProductsController.cs    # Endpoints de la API
│   └── Program.cs                   # Configuración DI
│
└── ProductAggregator.Core/
    ├── Models/                      # Modelos de dominio
    ├── Interfaces/                  # Contratos
    ├── Services/
    │   ├── ProductAggregatorService.cs  # Servicio a optimizar
    │   └── MockProviders/               # Simulan APIs externas
    └── Factories/
        └── ProviderFactory.cs       
```

## 🚀 Cómo Ejecutar

```bash
# Restaurar paquetes
dotnet restore

# Ejecutar la API
cd src/ProductAggregator.Api
dotnet run
```

## 🧪 Endpoints para Testing

### 1. Benchmark (Medir rendimiento)
```bash
curl "http://localhost:5000/api/products/benchmark?productCount=10"
```

### 2. Agregar múltiples productos
```bash
curl -X POST "http://localhost:5000/api/products/aggregate" \
  -H "Content-Type: application/json" \
  -d '{
    "productIds": ["PROD-001", "PROD-002", "PROD-003"],
    "includePrices": true,
    "includeStock": true
  }'
```

### 3. Obtener un producto
```bash
curl "http://localhost:5000/api/products/PROD-001"
```

## 📝 Tareas

1. Analizar el código actual en `ProductAggregatorService.cs`
2. Identificar los problemas de rendimiento
3. Proponer e implementar mejoras


## Análisis de Rendimiento y Mejoras Implementadas

## Enfoque de la Solución

El objetivo principal fue mejorar el rendimiento del servicio de agregación de productos reduciendo latencias generadas por llamadas a proveedores externos.

Las optimizaciones se enfocaron en tres aspectos clave:

- ejecución paralela de operaciones I/O
- control de concurrencia para proteger dependencias externas
- uso de caching para evitar trabajo repetido

## Problemas Identificados

### 1. Procesamiento potencialmente secuencial de productos

Cada request puede contener múltiples `productIds`.  
Si estos productos se procesan de manera secuencial, el tiempo total de respuesta crece rápidamente porque cada producto requiere consultar varios proveedores.

Por ejemplo:

Product 1 → consulta proveedores  
Product 2 → consulta proveedores  
Product 3 → consulta proveedores  

Esto hace que el tiempo total sea acumulativo.

---

### 2. Llamadas a proveedores externos

Para cada producto se realizan consultas a:

- 3 proveedores de precios
- 2 proveedores de stock

Si estas llamadas se ejecutan una tras otra, el tiempo total será la **suma de todas las latencias** de los proveedores, lo cual escala muy mal cuando el número de productos aumenta.

---

### 3. Falta de caching

El sistema recalculaba la agregación de un producto **cada vez que era solicitado**, incluso si el mismo producto se pedía varias veces seguidas.

En sistemas reales es común cachear resultados por períodos cortos para reducir la carga en servicios externos y mejorar el tiempo de respuesta.

---

### 4. Posible sobrecarga de proveedores externos

Si un request pide muchos productos (por ejemplo 50), y cada producto consulta 5 proveedores, el sistema podría generar hasta **250 llamadas externas simultáneas**.

Esto puede causar:

- saturación de APIs externas
- degradación de rendimiento
- errores por timeouts

---

## Mejoras Implementadas

### Procesamiento paralelo de productos

Se implementó paralelización utilizando `Task.WhenAll`, permitiendo que múltiples productos se agreguen al mismo tiempo en lugar de esperar a que uno termine para empezar el siguiente.

Esto reduce significativamente el tiempo total cuando se solicitan varios productos.

---

### Paralelización de llamadas a proveedores

Para cada producto, las llamadas a proveedores de precios y stock también se ejecutan en paralelo.

Esto permite que el tiempo de respuesta dependa del proveedor más lento, en lugar de ser la suma de todos.

---

### Control de concurrencia con SemaphoreSlim

Se introdujo un `SemaphoreSlim` para limitar el número de agregaciones que pueden ejecutarse al mismo tiempo.

```csharp
private static readonly SemaphoreSlim _semaphore = new(10);
```

con esto se evita picos de llamadas simultáneas hacia proveedores externos y protege al sistema de generar demasiadas operaciones concurrentes.

---

### Cache distribuido con Redis

Se agregó una capa de caching utilizando `IDistributedCache` con Redis.

El flujo funciona así:

1. Se genera una `cache key` basada en el `productId` y las opciones del request.
2. Se intenta recuperar el producto desde Redis.
3. Si existe en cache, se devuelve inmediatamente.
4. Si no existe, se realiza la agregación y luego se guarda en Redis.

Ejemplo de cache key:

```
product:{productId}:prices:{includePrices}:stock:{includeStock}
```

Los resultados se almacenan con una expiración de **5 minutos**, lo cual reduce llamadas repetidas a proveedores externos.

---

### Fallback en caso de fallo del cache

La integración con Redis se implementó dentro de bloques `try/catch`.  
Esto permite que el sistema siga funcionando normalmente incluso si Redis no está disponible.

En ese caso simplemente se omite el cache y se continúa con la agregación.

El API puede utilizar Redis o `DistributedMemoryCache` dependiendo de la configuración.

Si la bandera `Redis:Enabled` está en **true**, el sistema utiliza Redis como cache distribuido.


Comparto configuracion rapida de Redis para su prueba
```bash
docker run -d -p 6379:6379 redis
```

---

## Impacto en Rendimiento

Utilizando el endpoint de benchmark:

```
GET /api/products/benchmark?productCount=10
```

Las mejoras reducen significativamente el tiempo total de procesamiento gracias a:

- ejecución paralela de productos
- llamadas concurrentes a proveedores
- reutilización de resultados mediante cache

Cuando se solicitan productos repetidamente, ya que las respuestas pueden devolverse directamente desde Redis.

---

## Consideraciones de Diseño

Actualmente el `SemaphoreSlim` es estático, lo que significa que el límite de concurrencia es global para toda la aplicación.

Esto protege a los proveedores externos de sobrecargas, pero también implica que todas las requests comparten el mismo límite de concurrencia.

En un sistema de producción se podrían aplicar estrategias más avanzadas como:

- rate limiting
- políticas de resiliencia (por ejemplo con Polly)
- bulkhead isolation
- límites de concurrencia por proveedor

---

## Resumen

Las principales mejoras implementadas fueron:

- Procesamiento paralelo de productos
- Llamadas concurrentes a proveedores
- Control de concurrencia con `SemaphoreSlim`
- Cache distribuido usando Redis
- Manejo resiliente en caso de fallos

Estas mejoras permiten que el servicio escale mejor cuando se procesan múltiples productos y reducen significativamente la carga sobre los proveedores externos.


## resultados de consulta antes de los cambios
tiempo 20.12s
tamanio 259 B

{
    "productCount": 10,
    "processingTimeMs": 20001,
    "successfulProducts": 10,
    "averageTimePerProduct": 2000.1,
    "errors": []
}


## resultados de consulta al realizar cambios

{
    "productCount": 10,
    "processingTimeMs": 8,
    "successfulProducts": 10,
    "averageTimePerProduct": 0.8,
    "errors": []
}

Esto demuestra la mejora obtenida al evitar llamadas repetidas a proveedores externos y reutilizar los resultados agregados mediante caching.
