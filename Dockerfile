# The compiler runs on the build machine's own architecture and targets the image's.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY . .
RUN dotnet publish src/Nightwave.Server/Nightwave.Server.csproj -c Release -a "$TARGETARCH" -p:Version="$VERSION" -o /app
RUN cp LICENSE NOTICE packaging/picons-GPL-3.0.txt /app/

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV NIGHTWAVE_DATA=/data
VOLUME /data
EXPOSE 9985
# SAT>IP uses SSDP multicast and RTP over UDP: run with the host's network.
ENTRYPOINT ["./nightwaved"]
