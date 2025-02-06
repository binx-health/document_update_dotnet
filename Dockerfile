FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

RUN ls -la

COPY  ["./document_update_dotnet.csproj","document_update_dotnet/"]

RUN ls -la

RUN dotnet restore 'document_update_dotnet/document_update_dotnet.csproj'
COPY [".","document_update_dotnet/"]

RUN dotnet build 'document_update_dotnet/document_update_dotnet.csproj' -c Release -o /app/build

FROM build AS publish
RUN dotnet publish 'document_update_dotnet/document_update_dotnet.csproj' -c Release -o /app/publish


FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT [ "dotnet","document_update_dotnet.dll" ]
