# Shop platform (demo)

Two services:

- `src/checkout-api`: ASP.NET Core API. `POST /api/checkout` creates an order (OrderRepository, SQL Server) and authorises payment (PaymentClient).
- `src/search-api`: product search backed by Elasticsearch with a Redis read-through `ProductCache`. A nightly `CatalogueReindexJob` reloads the catalogue.
