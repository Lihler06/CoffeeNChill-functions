 CoffeeNChill Canteen Management System — Part 1

 Local Setup
1. Clone the repo: `git clone https://github.com/modestsnake/Coffee.git`
2. Ensure .NET 8 SDK and Azure Functions Core Tools are installed
3. Confirm `local.settings.json` has `"AzureWebJobsStorage": "UseDevelopmentStorage=true"`
4. Run `func start` from the project root
5. Test endpoints using the Postman collection found in `/docs`

 Docker — Standalone Execution (No Compose)

Prerequisites
- A real Azure Storage Account connection string for StaffDocsStorage

 1. Create the network
docker network create coffeenchill-net

 2. Run Azurite
docker run -d --name azurite --network coffeenchill-net -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite

3. Build the Functions image
docker build -t modestsnake/coffeenchill-functions:v1.0 .

4. Run the Functions container
docker run -d --name functions --network coffeenchill-net -p 7120:80 -e AzureWebJobsStorage="<connection string>" -e StorageConnection="<same>" -e StaffDocsStorage="<real Azure connection string>" modestsnake/coffeenchill-functions:v1.0

 Docker Hub
- https://hub.docker.com/r/modestsnake/coffeenchill-functions
- https://hub.docker.com/r/modestsnake/coffeenchill-azurite

Team Contributions
- [Asande Ngubane]: Standalone Dockerfile, Docker Hub image publishing (Functions + Azurite), container-to-Azurite networking setup, Postman collection creation and endpoint testing
- [ThatGuy-wandz]:Azure Files Integration (Staff Documents)
Implemented the staff-docs Azure File Share integration, including:
	•	Registered a ShareDirectoryClient in Program.cs, creating the staff-docs share on startup
	•	Built UploadStaffDocument (POST /api/documents/upload) — parses multipart/form-data and streams files into the file share
	•	Built ListStaffDocuments (GET /api/documents) — returns file name, size, and last modified date for all stored documents
	•	Built DownloadStaffDocument (GET /api/documents/download/{fileName}) — streams a requested file back to the client, with 404 handling for missing files
- [Tristyn ST10468574] : Azure Tables and HTTP Functions ( Menu management ) 
  [Lihlithemba Sithole ST10485223]: Testing Suite & Postman Collection
Postman collection completeness
Environment variable usage
(l(baseUrl)
Test coverage across all endpoint. 

Part 2: Docker Compose Orchestration
Contributor:ST10477821
 What it does
A single `docker-compose.yml` in the repo root starts the full stack:

 Service - Image (Docker Hub) -Purpose 
 azurite  `modestsnake/coffeenchill-azurite:v1.0` | Emulates Azure Tables, Blobs and Queues locally |
 functions  `st10477821/coffeenchill-functions:v2.0` | Azure Functions host with the HTTP and Queue triggers |

Both images are pulled from Docker Hub. The file has no `build:` paths.

Configuration
- Custom bridge network (`coffeenchill-net`) lets the Functions container reach Azurite by its service name.
- Named volume(`azurite-data`) keeps Azurite data when containers restart.
- Health checkon Azurite, with the Functions service set to wait for it before it starts.
- Connection string is passed through the `AzureWebJobsStorage` environment variable and points to the `azurite` host, not `127.0.0.1`.
- - Ports:Functions are exposed on `7120`, and Azurite on `10000`, `10001` and `10002`.
  
 How to run
Make sure Docker Desktop is running, then from the repo root:
docker compose up -d
The API is then available at `http://localhost:7120/api`.
To stop the stack:
docker compose down
Add `-v` to also delete the stored Azurite data.  
 

  How to test
1.GET http://localhost:7120/api/menu returns the menu as JSON.
2. POST http://localhost:7120/api/orders/queue with a JSON order body places the order on the queue.
3.GET http://localhost:7120/api/orders/status/{orderId} shows the status moving Received, Preparing, Ready, Collected.

The Postman collections in the repo can be used for these tests.





