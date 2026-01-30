# StudentService

A .NET 8 Blazor WebAssembly application for student job matching and recommendation services.

## ?? Features

- **Job Recommendations**: ML-powered job matching for students
- **Student Recommendations**: Help companies find the right students
- **Job Pay Prediction**: Predict hourly pay for job postings
- **Full-text Search**: Elasticsearch-powered job search
- **Rating System**: Thumbs up/down feedback for better recommendations

## ??? Architecture

- **Frontend**: Blazor WebAssembly
- **Backend API**: ASP.NET Core 8 Web API
- **Database**: MongoDB
- **Search**: Elasticsearch
- **ML Tracking**: MLflow
- **Load Balancing**: NGINX

## ?? Prerequisites

- .NET 8 SDK
- Docker & Docker Compose
- MongoDB (local or cloud)
- Elasticsearch (local or cloud)

## ?? Configuration

### 1. Create Environment File

Copy the example environment file and fill in your secrets:

```bash
cp .env.example .env
```

Edit `.env` with your actual values:

```env
ELASTICSEARCH_URI=https://your-elasticsearch-endpoint.elastic.cloud:443
ELASTICSEARCH_APIKEY=your-elasticsearch-api-key
OPENAI_APIKEY=your-openai-or-gemini-api-key
```

### 2. Local Development

For local development, create `StudentService.API/appsettings.Development.json`:

```json
{
  "OpenAI": {
    "ApiKey": "your-api-key"
  },
  "Elasticsearch": {
    "Uri": "your-elasticsearch-uri",
    "ApiKey": "your-api-key"
  }
}
```

## ?? Running with Docker

```bash
# Start all services
docker-compose up -d

# View logs
docker-compose logs -f

# Stop services
docker-compose down
```

The application will be available at:
- **Web App**: http://localhost:5001
- **API (via NGINX)**: http://localhost
- **MLflow UI**: http://localhost:5000

## ?? Running Locally

```bash
# Restore packages
dotnet restore

# Run the API
cd StudentService.API
dotnet run

# In another terminal, run the WebApp
cd StudentService.WebApp/StudentService.WebApp
dotnet run
```

## ?? Project Structure

```
??? StudentService.API/          # ASP.NET Core Web API
??? StudentService.Domain/       # Domain models and interfaces
??? StudentService.Infrastructure/  # Data access and services
??? StudentService.WebApp/       # Blazor WebAssembly frontend
??? nginx/                       # NGINX configuration
??? scripts/                     # Utility scripts
??? docker-compose.yml          # Docker orchestration
```

## ?? Security Notes

- Never commit `.env` files with real secrets
- Use `appsettings.Development.json` for local development (ignored by git)
- Store production secrets in environment variables or a secrets manager

## ?? License

This project is for educational purposes.
