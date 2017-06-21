﻿using Kentor.AuthServices.Exceptions;
using Kentor.AuthServices.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Metadata;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Kentor.AuthServices.Metadata
{
    /// <summary>
    /// Helper for loading SAML2 metadata
    /// </summary>
    public static class MetadataLoader
    {
        /// <summary>
        /// Load and parse metadata.
        /// </summary>
        /// <param name="metadataLocation">Path to metadata. A Url, absolute
        /// path or an app relative path (e.g. ~/App_Data/metadata.xml)</param>
        /// <returns>EntityDescriptor containing metadata</returns>
        public static ExtendedEntityDescriptor LoadIdp(string metadataLocation)
        {
            return LoadIdp(metadataLocation, false);
        }
        
        internal const string LoadIdpFoundEntitiesDescriptor = "Tried to load metadata for an IdentityProvider, which should be an <EntityDescriptor>, but found an <EntitiesDescriptor>. To load that metadata you should use the Federation configuration and not an IdentityProvider. You can also set the SPOptions.Compatibility.UnpackEntitiesDescriptorInIdentityProviderMetadata option to true.";
        internal const string LoadIdpUnpackingFoundMultipleEntityDescriptors = "Unpacked an EntitiesDescriptor when loading idp metadata, but found multiple EntityDescriptors.Unpacking is only supported if the metadata contains a single EntityDescriptor. Maybe you should use a Federation instead of configuring a single IdentityProvider";

        /// <summary>
        /// Load and parse metadata.
        /// </summary>
        /// <param name="metadataLocation">Path to metadata. A Url, absolute
        /// path or an app relative path (e.g. ~/App_Data/metadata.xml)</param>
        /// <param name="unpackEntitiesDescriptor">If the metadata contains
        /// an EntitiesDescriptor, try to unpack it and return a single
        /// EntityDescriptor inside if there is one.</param>
        /// <returns>EntityDescriptor containing metadata</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EntityDescriptors")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "SPOptions")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "UnpackEntitiesDescriptorInIdentityProviderMetadata")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EntitiesDescriptor")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EntityDescriptor")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "IdentityProvider")]
        public static ExtendedEntityDescriptor LoadIdp(string metadataLocation, bool unpackEntitiesDescriptor)
        {
            if (metadataLocation == null)
            {
                throw new ArgumentNullException(nameof(metadataLocation));
            }

            var result = Load(metadataLocation, null, null);

            var entitiesDescriptor = result as ExtendedEntitiesDescriptor;
            if(entitiesDescriptor != null)
            {
                if(unpackEntitiesDescriptor)
                {
                    if(entitiesDescriptor.ChildEntities.Count > 1)
                    {
                        throw new InvalidOperationException(LoadIdpUnpackingFoundMultipleEntityDescriptors);
                    }

                    return (ExtendedEntityDescriptor)entitiesDescriptor.ChildEntities.Single();
                }

                throw new InvalidOperationException(LoadIdpFoundEntitiesDescriptor);
            }

            return (ExtendedEntityDescriptor)result;
        }

        private static MetadataBase Load(
            string metadataLocation,
            IEnumerable<SecurityKeyIdentifierClause> signingKeys,
            string minIncomingSigningAlgorithm)
        {
            if(PathHelper.IsWebRootRelative(metadataLocation))
            {
                metadataLocation = PathHelper.MapPath(metadataLocation);
            }

            using (var client = new WebClient())
            using (var stream = client.OpenRead(metadataLocation))
            {
                var reader = XmlDictionaryReader.CreateTextReader(
                    stream,
                    XmlDictionaryReaderQuotas.Max);

                reader = ValidateSignature(reader, signingKeys, minIncomingSigningAlgorithm);

                return Load(reader);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "No unmanaged resources involved, safe to ignore")]
        private static XmlDictionaryReader ValidateSignature(
            XmlDictionaryReader reader,
            IEnumerable<SecurityKeyIdentifierClause> signingKeys,
            string minIncomingSigningAlgorithm)
        {
            if (signingKeys == null)
                return reader;

            var xmlDoc = XmlHelpers.CreateSafeXmlDocument();
            xmlDoc.Load(reader);

            if(!xmlDoc.DocumentElement.IsSignedByAny(signingKeys, false, minIncomingSigningAlgorithm))
            {
                throw new InvalidSignatureException("Signature validation failed for federation metadata.");
            }

            return XmlDictionaryReader.CreateDictionaryReader(
                new XmlNodeReader(xmlDoc));
        }

        internal static MetadataBase Load(XmlDictionaryReader reader)
        {
            var serializer = ExtendedMetadataSerializer.ReaderInstance;
            
            // Filter out the signature from the metadata, as the built in MetadataSerializer
            // doesn't handle the XmlDsigNamespaceUrl http://www.w3.org/2000/09/xmldsig# which
            // is allowed (and for SAMLv1 even recommended).
            using (var filter = new FilteringXmlDictionaryReader(SignedXml.XmlDsigNamespaceUrl, "Signature", reader))
            {
                return serializer.ReadMetadata(filter);
            }
        }

        internal const string LoadFederationFoundEntityDescriptor = "Tried to load metadata for a Federation, which should be an <EntitiesDescriptor> containing one or more <EntityDescriptor> elements, but found an <EntityDescriptor>. To load that metadata you should use the IdentityProvider configuration and not a Federation.";

        /// <summary>
        /// Load and parse metadata for a federation.
        /// </summary>
        /// <param name="metadataLocation">Url to metadata</param>
        /// <returns>Extended entitiesdescriptor</returns>
        public static ExtendedEntitiesDescriptor LoadFederation(string metadataLocation)
        {
            return LoadFederation(metadataLocation, null, null);
        }

        /// <summary>
        /// Load and parse metadata for a federation.
        /// </summary>
        /// <param name="metadataLocation">Url to metadata</param>
        /// <param name="signingKeys"></param>
        /// <param name="minIncomingSigningAlgorithm">Mininum strength accepted
        /// for signing algorithm.</param>
        /// <returns>Extended entitiesdescriptor</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EntitiesDescriptor")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EntityDescriptor")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "IdentityProvider")]
        public static ExtendedEntitiesDescriptor LoadFederation(
            string metadataLocation,
            IEnumerable<SecurityKeyIdentifierClause> signingKeys,
            string minIncomingSigningAlgorithm)
        {
            if (metadataLocation == null)
            {
                throw new ArgumentNullException(nameof(metadataLocation));
            }

            var result = Load(metadataLocation, signingKeys, minIncomingSigningAlgorithm);

            if (result is ExtendedEntityDescriptor)
            {
                throw new InvalidOperationException(LoadFederationFoundEntityDescriptor);
            }

            return (ExtendedEntitiesDescriptor)result;
        }

    }
}
