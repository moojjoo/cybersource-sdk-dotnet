using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using CyberSource.Base;


namespace CyberSource.Clients
{
    /// <summary>
    /// CyberSource Web Services XML Client class.
    /// </summary>
    public class XmlClient : BaseClient
    {
   
        private const string SOAP_ENVELOPE =
           "<soap:Envelope xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#/\" xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:wsse=\"http://schemas.xmlsoap.org/ws/2002/04/secext\">" +
           "<soap:Header><wsse:Security></wsse:Security><wsse:BinarySecurityToken EncodingType=\"wsse:Base64Binary\" Id=\"X509Token\" ValueType=\"wsse:X509v3\"></wsse:BinarySecurityToken></soap:Header>" +
           "<soap:Body Id=\"MsgBody\"></soap:Body></soap:Envelope>";


        private const string REQUEST_MESSAGE = "requestMessage";
        private const string REPLY_MESSAGE = "replyMessage";
        private const string MERCHANT_REFERENCE_CODE = "merchantReferenceCode";

        private static XmlDocument mSoapEnvelope;

        static XmlClient()
        {
            // load the SOAP envelope document.
            mSoapEnvelope = new XmlDocument();



            mSoapEnvelope.LoadXml(SOAP_ENVELOPE);
        }

        private XmlClient() { }

        /// <summary>
        /// Sends a CyberSource transaction request.
        /// </summary>
        /// <param name="request">XmlDocument object containing the request.</param>
        /// <returns>XmlDocument object containing the reply.</returns>
        public static XmlDocument RunTransaction(XmlDocument request)
        {
            return (RunTransaction(null, request));
        }

        /// <summary>
        /// Sends a CyberSource transaction request.
        /// </summary>
        /// <param name="request">XmlDocument object containing the request.</param>
        /// <param name="config">Configuration object to use.</param>
        /// <returns>XmlDocument object containing the reply.</returns>
        public static XmlDocument RunTransaction(
            Configuration config, XmlDocument request)
        {

            Logger logger = null;
            string nspace = null;
            try
            {

                nspace = GetRequestNamespace(request);
                DetermineEffectiveMerchantID(ref config, request, nspace);
                SetVersionInformation(request, nspace);
                logger = PrepareLog(config);
                
                if (string.IsNullOrEmpty(nspace))
                {
                    throw new ApplicationException(
                        REQUEST_MESSAGE + " is missing in the XML document.");
                }

                
                SetConnectionLimit(config);

                if (logger != null)
                {
                    logger.LogRequest(request, config.Demo);
                }

                // obtain a copy of the request document enclosed in a SOAP envelope
                XmlDocument doc = SoapWrap(request, nspace);

                //Get the X509 cert and sign the SOAP Body    
                string keyFilePath = Path.Combine(config.KeysDirectory, config.EffectiveKeyFilename);

                X509Certificate2 cert = null;

                X509Certificate2Collection collection = new X509Certificate2Collection();
                collection.Import(keyFilePath, config.EffectivePassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                foreach (X509Certificate2 cert1 in collection)
                {
                    if (cert1.Subject.Contains(config.MerchantID))
                    {
                        cert = cert1;
                        break;
                    }
                }
                
                SignDocument(cert, doc);

                // convert the document into an array of bytes using the
                // encoding specified in the XML declaration line.
                Encoding enc = GetEncoding(doc);
                byte[] requestBytes = enc.GetBytes(doc.OuterXml);


                // create an HttpWebRequest object and set its properties
                HttpWebRequest httpRequest
                    = (HttpWebRequest)WebRequest.Create(
                        config.EffectiveServerURL);
                httpRequest.Method = "POST";
                httpRequest.ContentLength = requestBytes.Length;
                httpRequest.UserAgent = ".NET XML";

                // set the timeout
                httpRequest.Timeout = config.Timeout * 1000;

                if (mProxy != null)
                {
                    // assign our pre-created WebProxy object to the 
                    // HttpWebRequest object's Proxy property.
                    httpRequest.Proxy = mProxy;
                }

                // obtain the request stream and write the byte array to it.
                Stream stream = httpRequest.GetRequestStream();
                stream.Write(requestBytes, 0, requestBytes.Length);
                stream.Close();

                // send request and get response.
                WebResponse webResponse = httpRequest.GetResponse();

                // read returned XML document.
                XmlDocument reply = ReadXml(webResponse);

                XmlDocument unwrapped = SoapUnwrap(reply, nspace);

                if (logger != null)
                {
                    logger.LogReply(unwrapped, config.Demo);
                }

                // return the XML document without the SOAP envelope.
                return (unwrapped);
            }
            catch (WebException we)
            {
                // if we got HTTP status 500 (Internal Server Error), it could
                // mean, the server threw a fault, in which case, we load the
                // xml document from the HTTP body and throw a FaultException.

                // The status would be ProtocolError if we did get HTTP 500
                // and Response should not be null but we check for null just
                // in case.
                if (we.Status == WebExceptionStatus.ProtocolError &&
                    we.Response != null)
                {
                    HttpWebResponse response = (HttpWebResponse)we.Response;

                    // InternalServerError corresponds to HTTP 500.  And we
                    // proceed only if there's anything in the response body.
                    // That is, the contentLength is greater than zero.
                    if (response.StatusCode
                        == HttpStatusCode.InternalServerError &&
                        response.ContentLength > 0)
                    {
                        try
                        {
                            // read the fault document and throw a
                            // FaultException.
                            FaultException fe
                                = new FaultException(
                                    ReadXml(response), nspace);
                            if (logger != null)
                            {
                                logger.LogFault(fe.LogString);
                            }
                            throw;
                        }
                        catch (XmlException)
                        {
                            if (logger != null)
                            {
                                logger.LogException(we);
                            }

                            // the response body is not a valid xml document.
                            // It is therefore not a fault.  Rethrow the
                            // WebException object.
                            throw;
                        }

                    }
                }

                if (logger != null)
                {
                    logger.LogException(we);
                }

                // the server did not throw a fault.  Rethrow the WebException
                // object.
                throw;
            }
            catch (Exception e)
            {
                if (logger != null)
                {
                    logger.LogException(e);
                }
                throw;
            }
        }

        private static void SignDocument(X509Certificate2 cert, XmlDocument doc)
        {
            
            //Create reference to #MsgBody which is the ID of the SOAP Body (only signing the Body)
            Reference reference = new Reference("#MsgBody");

            //Add an enveloped transformation to the reference.
            XmlDsigEnvelopedSignatureTransform env = new XmlDsigEnvelopedSignatureTransform();
            reference.AddTransform(env);


            //Create a signedXML object from doc, add reference and private key, then generate the signature
            SignedXml signedXML = new SignedXml(doc);
            signedXML.AddReference(reference);
            
            RSACryptoServiceProvider rsaKey = (RSACryptoServiceProvider)cert.PrivateKey;
            signedXML.SigningKey = rsaKey;

            // Changes done to support SHA2 - START ===================
            var cn14Transform = new XmlDsigExcC14NTransform();

            string referenceDigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256";
            reference.AddTransform(cn14Transform);
            reference.DigestMethod = referenceDigestMethod;
            signedXML.AddReference(reference);

            KeyedHashAlgorithm kha = KeyedHashAlgorithm.Create("RSA-SHA256");
            // Changes done to support SHA2 - END ===================
            
            // Compute the signature.
            signedXML.ComputeSignature();

            // Get the XML representation of the signature and add to the SOAP Header
            XmlElement xmlDigitalSignature = signedXML.GetXml();
            doc.DocumentElement.FirstChild.FirstChild.AppendChild(doc.ImportNode(xmlDigitalSignature, true));

            // Add KeyInfo Node with reference to the X509 cert
            XmlDocument keyInfo = new XmlDocument();
            keyInfo.LoadXml("<root xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" xmlns:wsse=\"http://schemas.xmlsoap.org/ws/2002/04/secext\" ><ds:KeyInfo><SecurityTokenReference xmlns=\"http://schemas.xmlsoap.org/ws/2002/04/secext\"><wsse:Reference URI=\"#X509Token\"/></SecurityTokenReference></ds:KeyInfo></root>");
            doc.DocumentElement.FirstChild.FirstChild.LastChild.AppendChild(doc.ImportNode(keyInfo.FirstChild.FirstChild, true));

            //Add The Base64 representation of the X509 cert to BinarySecurityToken Node
            //X509SecurityToken token = new X509SecurityToken(cert);
            doc.DocumentElement.FirstChild.LastChild.InnerText = Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.None);

        }

        /// <summary>
        /// Returns the namespace URI used for requestMessage.
        /// </summary>
        /// <param name="request">XmlDocument containing requestMessage</param>
        /// <returns>
        /// the namespace URI used for requestMessage or null
        /// if there is no requestMessage in the document.
        /// </returns>
        public static string GetRequestNamespace(XmlDocument request)
        {
            if (request == null)
                return null;

            XmlNodeList list = request.ChildNodes;
            foreach (XmlNode node in list)
            {
                if (REQUEST_MESSAGE.Equals(node.LocalName))
                {
                    return (node.NamespaceURI);
                }
            }

            return (null);
        }

        private static void DetermineEffectiveMerchantID(
            ref Configuration config, XmlDocument request, string nspace)
        {
            string requestMerchantID = GetMerchantID(request, nspace);

            if (config == null)
            {
                // let's build a config object on the fly using
                // the merchantID from the request.  An exception will
                // be thrown if requestMerchantID is null and 
                // no merchantID is found in the config file.
                config = BuildConfigurationForRequest(requestMerchantID);
            }

            if (requestMerchantID == null)
            {
                // No merchantID in the request; get it from the config.
                // NonNullMerchantID will throw an exception if
                // MerchantID is null.
                SetMerchantID(request, config.NonNullMerchantID, nspace);
            }
            // else, there is a merchantID in the request.
            // we do not have to do anything.  We'll keep whatever
            // merchantID is in the Configuration object as we do
            // not own that object.
        }

        private static string GetMerchantID(
            XmlDocument request, string nspace)
        {
            string merchantID = null;
            XmlNode merchantIDNode = GetNode(request, MERCHANT_ID, nspace);
            if (merchantIDNode != null &&
                merchantIDNode.ChildNodes != null &&
                merchantIDNode.ChildNodes.Count > 0)
            {
                merchantID = merchantIDNode.ChildNodes[0].Value;
            }

            return (merchantID);
        }

        private static void SetMerchantID(
            XmlDocument request, string merchantID, string nspace)
        {
            XmlNode requestMessageNode
                = GetNode(request, REQUEST_MESSAGE, nspace);
            if (requestMessageNode == null)
            {
                throw new ApplicationException(
                    "INVALID XML REQUEST:  requestMessage is missing!");
            }
            XmlNode previousSibling = null;
            SetField(
                requestMessageNode, ref previousSibling,
                MERCHANT_ID, merchantID, nspace);
        }

        private static XmlDocument ReadXml(WebResponse webResponse)
        {
            Stream stream = null;
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.PreserveWhitespace = true;
                stream = webResponse.GetResponseStream();
                xmlDoc.Load(stream);
                return (xmlDoc);
            }
            finally
            {
                if (stream != null)
                {
                    stream.Close();
                }
            }
        }

        private static void SetVersionInformation(
            XmlDocument request, string nspace)
        {
            // obtain the requestMessage node.
            XmlNode requestMessageNode
                = GetNode(request, REQUEST_MESSAGE, nspace);

            // obtain the node that must precede the version fields according
            // to the schema.  If none is found, the "clientLibrary" element
            // will be inserted as the first child.
            XmlNode previousSibling
                = GetNode(request, MERCHANT_REFERENCE_CODE, nspace);
            if (previousSibling == null)
            {
                previousSibling = GetNode(request, MERCHANT_ID, nspace);
            }

            SetField(requestMessageNode, ref previousSibling,
                "clientLibrary", ".NET XML", nspace);
            SetField(requestMessageNode, ref previousSibling,
                "clientLibraryVersion", CLIENT_LIBRARY_VERSION,
                nspace);
            SetField(requestMessageNode, ref previousSibling,
                "clientEnvironment", mEnvironmentInfo, nspace);

            SetField(requestMessageNode, ref previousSibling,
                "clientSecurityLibraryVersion",
                ".Net 1.0.0", nspace);
        }

        private static void SetField(
            XmlNode parentNode, ref XmlNode previousSibling,
            string tagName, string tagValue, string nspace)
        {
            // obtain a pointer to the XmlDocument object.  This is used
            // in a few places in this method.
            XmlDocument doc = parentNode.OwnerDocument;

            // create an XmlText object to hold the field's value.
            XmlText text = doc.CreateTextNode(tagValue);

            // look for the field.
            XmlNode node = GetNode(doc, tagName, nspace);

            // if the field does not exist,...
            if (node == null)
            {
                // create an element for it and inside this element,
                // insert the XmlText object we created earlier.
                node = doc.CreateElement(tagName, nspace);
                node.AppendChild(text);

                // if there is a previous sibling, insert the new node
                // after it.
                if (previousSibling != null)
                {
                    parentNode.InsertAfter(node, previousSibling);
                }
                // else, the new node becomes the first child.
                else
                {
                    parentNode.PrependChild(node);
                }
            }
            // else, if the field already exists, replace its value.
            else
            {
                // if the field does have a value, replace it with
                // the XmlText object we created earlier.
                if (node.HasChildNodes)
                {
                    node.ReplaceChild(text, node.ChildNodes[0]);
                }
                // else, if it's empty, append the XmlText object
                // we created earlier.
                else
                {
                    node.AppendChild(text);
                }
            }

            // the next node to be added will be after this node.  So, we
            // set previousSibling to this node.
            previousSibling = node;
        }

        private static Encoding GetEncoding(XmlDocument doc)
        {
            if (doc.HasChildNodes && doc.ChildNodes[0] is XmlDeclaration)
            {
                XmlDeclaration node = (XmlDeclaration)doc.ChildNodes[0];
                if (node.Encoding != String.Empty)
                {
                    return (Encoding.GetEncoding(node.Encoding));
                }
            }

            // Default is UTF-8.
            return (Encoding.UTF8);
        }

        private static XmlDocument SoapWrap(
            XmlDocument doc, string nspace)
        {
            XmlNode xmlDeclaration = GetXmlDeclarationNode(doc);
            XmlNode requestMessage
                = GetNode(doc, REQUEST_MESSAGE, nspace);

            XmlDocument wrapped = new XmlDocument();

            if (xmlDeclaration != null)
            {
                wrapped.AppendChild(
                    wrapped.ImportNode(xmlDeclaration, true));
            }

            wrapped.AppendChild(
                wrapped.ImportNode(mSoapEnvelope.FirstChild, true));

            if (requestMessage != null)
            {
                wrapped.LastChild.LastChild.AppendChild(
                    wrapped.ImportNode(requestMessage, true));
            }

            return (wrapped);
        }

        private static XmlDocument SoapUnwrap(
            XmlDocument doc, string nspace)
        {
            XmlNode xmlDeclaration = GetXmlDeclarationNode(doc);
            XmlNode replyMessage
                = GetNode(doc, REPLY_MESSAGE, nspace);

            XmlDocument unwrapped = new XmlDocument();

            if (xmlDeclaration != null)
            {
                unwrapped.AppendChild(
                    unwrapped.ImportNode(xmlDeclaration, true));
            }

            if (replyMessage != null)
            {
                unwrapped.AppendChild(
                    unwrapped.ImportNode(replyMessage, true));
            }

            return (unwrapped);
        }

        private static XmlNode GetXmlDeclarationNode(XmlDocument doc)
        {
            if (doc.HasChildNodes && doc.ChildNodes[0] is XmlDeclaration)
            {
                return (doc.ChildNodes[0]);
            }

            return (null);
        }

        private static XmlNode GetNode(
            XmlDocument doc, string tagName, string nspace)
        {
            XmlNodeList nodes
                = doc.GetElementsByTagName(tagName, nspace);
            if (nodes != null && nodes.Count > 0)
            {
                return (nodes[0]);
            }

            return (null);
        }

    }
}
