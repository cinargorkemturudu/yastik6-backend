# Stage 1: Builder (Belirtilen tam SDK versiyonu)
FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build
WORKDIR /src

# Test projesini dahil etmeden sadece ana projenin dosyasını kopyalıyoruz
COPY ["Yastık6.Api/Yastık6.Api.csproj", "Yastık6.Api/"]
RUN dotnet restore "Yastık6.Api/Yastık6.Api.csproj"

# Kalan tüm dosyaları kopyalıyoruz
COPY . .

# Çalışma dizinini ana projenin içine alıp publish işlemini başlatıyoruz
WORKDIR "/src/Yastık6.Api"
RUN dotnet publish "Yastık6.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Final (Hafif Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Derlenen dosyaları 1. aşamadan alıp buraya kopyalıyoruz
COPY --from=build /app/publish .

# SQLite veritabanının ve logların kaydedileceği klasörleri oluşturup yetkilendiriyoruz
RUN mkdir -p /app/logs && chmod 777 /app/logs
RUN mkdir -p /app/data && chmod 777 /app/data

# ASP.NET Core 8 ve sonrasında varsayılan port 8080'dir. Onu dışarı açıyoruz.
EXPOSE 8080

# Çevresel değişkenleri ayarlıyoruz
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# SQLite dosyasının doğru klasöre yazılması için ConnectionString'i eziyoruz
ENV ConnectionStrings__DefaultConnection="Data Source=/app/data/yastik.db"

# Uygulamayı başlat
ENTRYPOINT ["dotnet", "Yastık6.Api.dll"]